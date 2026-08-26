using System.Net;
using System.Text;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;
using EtlTool.Application.Sources;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Web.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static EtlTool.IntegrationTests.Extraction.OpenXmlWorkbookFixture;

namespace EtlTool.IntegrationTests.Web;

public sealed class PipelinePreviewMvcTests
{
    [Fact]
    public async Task Preview_GetRendersActualTransformedCountersAndRowErrorComposition()
    {
        var pipeline = Pipeline();
        var csv = "Name;Email\n Ada ;ada@example.test\n Bob ;invalid\n Skip ;skip@example.test";
        await using var host = await PreviewHost.StartAsync(
            pipeline,
            Encoding.UTF8.GetBytes(csv));

        using var response = await host.Client.GetAsync($"/Pipelines/Preview/{pipeline.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Preview pipeline", html);
        Assert.Contains("<div class=\"text-muted\">Valid</div><div class=\"fs-3\">1</div>", html);
        Assert.Contains("<div class=\"text-muted\">Invalid</div><div class=\"fs-3\">1</div>", html);
        Assert.Contains("<div class=\"text-muted\">Filtered</div><div class=\"fs-3\">1</div>", html);
        Assert.Contains(">Ada<", html);
        Assert.Contains(">Bob<", html);
        Assert.Contains(">email<", html);
        Assert.Contains(">Validation<", html);
        Assert.Contains("must be a valid email address", html);
    }

    [Fact]
    public async Task Preview_GetUsesThePersistedXlsxWorksheet()
    {
        var pipeline = XlsxPipeline();
        await using var workbook = Create(
            Sheet("Ignored", Row(Text(1, "Id")), Row(Number(1, 999))),
            Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 42))));
        await using var bytes = new MemoryStream();
        await workbook.CopyToAsync(bytes);
        await using var host = await PreviewHost.StartAsync(pipeline, bytes.ToArray());

        using var response = await host.Client.GetAsync($"/Pipelines/Preview/{pipeline.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">42<", html);
        Assert.DoesNotContain(">999<", html);
    }

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Customer import",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            Delimiter = CsvDelimiter.Semicolon,
            FirstRowIsHeader = true
        },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Name" },
            new SourceFieldDefinition { Name = "Email" }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Name", TargetField = "name" },
            new FieldMapping { SourceField = "Email", TargetField = "email" }
        ],
        TransformationRules =
        [
            new TransformationRule
            {
                Id = Guid.NewGuid(),
                Order = 1,
                Type = TransformationType.Trim,
                SourceField = "name"
            },
            new TransformationRule
            {
                Id = Guid.NewGuid(),
                Order = 2,
                Type = TransformationType.FilterRow,
                SourceField = "name",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Operator"] = FilterOperator.Equals.ToString(),
                    ["Value"] = "Skip"
                }
            }
        ],
        ValidationRules =
        [
            new ValidationRule
            {
                Id = Guid.NewGuid(),
                Type = ValidationType.EmailFormat,
                Field = "email"
            }
        ],
        DestinationDatabase = "demo",
        DestinationCollection = "customers",
        UpsertKeyField = "email"
    };

    private static PipelineDefinition XlsxPipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Workbook import",
        SourceType = SourceType.Xlsx,
        SourceOptions = new SourceOptions
        {
            WorksheetName = "Data",
            FirstRowIsHeader = true
        },
        ExpectedSchema = [new SourceFieldDefinition { Name = "Id" }],
        FieldMappings = [new FieldMapping { SourceField = "Id", TargetField = "id" }],
        DestinationDatabase = "demo",
        DestinationCollection = "customers",
        UpsertKeyField = "id"
    };

    private sealed class PreviewHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private PreviewHost(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<PreviewHost> StartAsync(
            PipelineDefinition pipeline,
            byte[] source)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services
                .AddControllersWithViews()
                .AddApplicationPart(typeof(PipelinesController).Assembly);

            builder.Services.AddSingleton<IPipelineDefinitionRepository>(
                new SinglePipelineRepository(pipeline));
            builder.Services.AddSingleton<IPipelineService>(new SinglePipelineService(pipeline));
            builder.Services.AddSingleton<IWizardSourceStore>(new MemoryWizardSourceStore(source));
            builder.Services.AddSingleton<PipelineSourceCommitCoordinator>();
            builder.Services.AddSingleton<FieldMappingService>();
            builder.Services.AddSingleton<IMongoTargetAccessService, AllowedTargetAccessService>();
            builder.Services.AddSingleton<IPipelineReadinessService, PipelineReadinessService>();
            builder.Services.AddSingleton<CsvFileExtractor>();
            builder.Services.AddSingleton<XlsxFileExtractor>();
            builder.Services.AddSingleton<IFileExtractor>(provider =>
                provider.GetRequiredService<CsvFileExtractor>());
            builder.Services.AddSingleton<IFileExtractor>(provider =>
                provider.GetRequiredService<XlsxFileExtractor>());
            builder.Services.AddSingleton<IFileExtractorResolver, FileExtractorResolver>();
            builder.Services.AddSingleton<ITransformationHandler, TrimTransformationHandler>();
            builder.Services.AddSingleton<ITransformationHandler, ConditionalFilterTransformationHandler>();
            builder.Services.AddSingleton<TransformationHandlerRegistry>();
            builder.Services.AddSingleton<TransformationEngine>();
            builder.Services.AddSingleton<IValidationHandler, EmailValidationHandler>();
            builder.Services.AddSingleton<ValidationHandlerRegistry>();
            builder.Services.AddSingleton<ValidationEngine>();
            builder.Services.AddSingleton<PipelineRowProcessor>();
            builder.Services.AddSingleton<IPreviewService, PreviewService>();

            var application = builder.Build();
            application.MapControllers();
            application.MapControllerRoute(
                name: "default",
                pattern: "{controller}/{action}/{id?}");
            await application.StartAsync();

            var address = application.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new PreviewHost(application, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class SinglePipelineService(PipelineDefinition pipeline) : IPipelineService
    {
        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SinglePipelineRepository(PipelineDefinition pipeline) : IPipelineDefinitionRepository
    {
        public Task AddAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryWizardSourceStore(byte[] content) : IWizardSourceStore
    {
        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IWizardSourceLease?>(new MemoryLease(new MemoryStream(content)));

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class MemoryLease(Stream content) : IWizardSourceLease
        {
            public Stream Content { get; } = content;

            public ValueTask DisposeAsync() => Content.DisposeAsync();
        }
    }

    private sealed class AllowedTargetAccessService : IMongoTargetAccessService
    {
        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
