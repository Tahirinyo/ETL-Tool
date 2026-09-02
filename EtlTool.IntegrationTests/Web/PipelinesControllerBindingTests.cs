using System.Net;
using EtlTool.Application.Connections;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.IntegrationTests.MongoDB;
using EtlTool.Web.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Text.RegularExpressions;

namespace EtlTool.IntegrationTests.Web;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class PipelinesControllerBindingTests(MongoDbFixture mongoDbFixture)
{
    [Fact]
    public async Task TransformationRulesIndex_RendersRuleScopedEditAndAntiForgeryProtectedDeleteActions()
    {
        var pipeline = CreatePipeline("Route pipeline");
        var rule = new TransformationRule
        {
            Id = Guid.NewGuid(),
            Type = TransformationType.Trim,
            Order = 1,
            SourceField = "name"
        };
        pipeline.TransformationRules = [rule];

        await using var host = await BindingTestHost.StartAsync(new RecordingPipelineService(pipeline));

        using var response = await host.Client.GetAsync($"/Pipelines/{pipeline.Id}/Transformations");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var editAction = $"/Pipelines/{pipeline.Id}/Transformations/{rule.Id}/Edit";
        Assert.Matches(
            $"<a\\b(?=[^>]*\\shref=\"{Regex.Escape(editAction)}\")[^>]*>",
            html);

        var deleteAction = $"/Pipelines/{pipeline.Id}/Transformations/{rule.Id}/Delete";
        var deleteFormStart = Regex.Match(
            html,
            $"<form\\b(?=[^>]*\\saction=\"{Regex.Escape(deleteAction)}\")(?=[^>]*\\smethod=\"post\")[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(deleteFormStart.Success, "The rule-specific delete POST form was not rendered.");
        var deleteFormEnd = html.IndexOf("</form>", deleteFormStart.Index, StringComparison.Ordinal);
        Assert.NotEqual(-1, deleteFormEnd);
        var deleteForm = html[deleteFormStart.Index..deleteFormEnd];
        Assert.Single(
            Regex.Matches(
                    deleteForm,
                    "<input\\b(?=[^>]*\\sname=\"__RequestVerificationToken\")[^>]*>",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>());
    }

    [Fact]
    public async Task EditPost_UsesRouteIdWhenFormContainsConflictingId()
    {
        var routePipeline = CreatePipeline("Route pipeline");
        var postedPipeline = CreatePipeline("Posted pipeline");
        var service = new RecordingPipelineService(routePipeline, postedPipeline);
        await using var host = await BindingTestHost.StartAsync(service);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();

        using var response = await host.Client.PostAsync(
            $"/Pipelines/Edit/{routePipeline.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = postedPipeline.Id.ToString(),
                ["Name"] = "Updated route pipeline",
                ["Description"] = "Route identity remained authoritative",
                ["__RequestVerificationToken"] = antiForgeryToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(routePipeline.Id, service.UpdatedId);
        Assert.Equal("Updated route pipeline", service.GetRequired(routePipeline.Id).Name);
        Assert.Equal("Posted pipeline", service.GetRequired(postedPipeline.Id).Name);
    }

    [Fact]
    public async Task EditPost_SavedPostgreSqlDestinationRedirectRendersPersistedHierarchyAsSelected()
    {
        var connectionId = Guid.Parse("26f731d6-2f20-4e35-a514-e265b00777aa");
        var pipeline = CreatePipeline("Mongo customers");
        pipeline.SourceType = SourceType.MongoDb;
        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "_id", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "email", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "name", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "balance", DataType = SourceFieldType.Decimal }
        ];
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "_id", TargetField = "CustomerId", IsIncluded = true },
            new FieldMapping { SourceField = "email", TargetField = "Email", IsIncluded = true },
            new FieldMapping { SourceField = "name", TargetField = "FullName", IsIncluded = true },
            new FieldMapping { SourceField = "balance", TargetField = "Balance", IsIncluded = true }
        ];
        var pipelineService = new RecordingPipelineService(pipeline);
        var savedConnectionService = new SavedConnectionServiceStub(connectionId);
        var savedMetadataDiscoveryService = new SavedPostgreSqlMetadataDiscoveryStub();
        await using var host = await BindingTestHost.StartAsync(
            pipelineService,
            savedConnectionService: savedConnectionService,
            savedMetadataDiscoveryService: savedMetadataDiscoveryService);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();

        using var post = await host.Client.PostAsync(
            $"/Pipelines/Edit/{pipeline.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = pipeline.Name,
                ["DestinationType"] = DestinationType.PostgreSql.ToString(),
                ["PostgreSqlDestinationSavedConnectionId"] = connectionId.ToString(),
                ["PostgreSqlDatabase"] = "etl_demo",
                ["PostgreSqlSchema"] = "etl_demo",
                ["PostgreSqlTable"] = "mongo_customers",
                ["PostgreSqlColumnMappings[0].OutputField"] = "CustomerId",
                ["PostgreSqlColumnMappings[0].DestinationColumn"] = "source_id",
                ["PostgreSqlColumnMappings[1].OutputField"] = "Email",
                ["PostgreSqlColumnMappings[1].DestinationColumn"] = "email",
                ["PostgreSqlColumnMappings[2].OutputField"] = "FullName",
                ["PostgreSqlColumnMappings[2].DestinationColumn"] = "customer_name",
                ["PostgreSqlColumnMappings[3].OutputField"] = "Balance",
                ["PostgreSqlColumnMappings[3].DestinationColumn"] = "balance",
                ["PostgreSqlUpsertKeyColumn"] = "source_id",
                ["__RequestVerificationToken"] = antiForgeryToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal($"/Pipelines/Edit/{pipeline.Id}#destination", post.Headers.Location!.OriginalString);
        var saved = pipelineService.GetRequired(pipeline.Id);
        Assert.Equal(("etl_demo", "etl_demo", "mongo_customers"),
            (saved.PostgreSqlDestination!.Database, saved.PostgreSqlDestination.Schema, saved.PostgreSqlDestination.Table));
        Assert.Equal("source_id", saved.PostgreSqlDestination.UpsertKeyColumn);
        Assert.Equal("CustomerId", saved.UpsertKeyField);

        using var get = await host.Client.GetAsync(post.Headers.Location);
        var html = await get.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertSelectedOption(html, "destination-postgresql-connection", connectionId.ToString());
        AssertSelectedOption(html, "destination-postgresql-database", "etl_demo");
        AssertSelectedOption(html, "destination-postgresql-schema", "etl_demo");
        AssertSelectedOption(html, "destination-postgresql-table", "mongo_customers");
    }

    [Fact]
    public async Task EditPost_BrowserPlaceholderMappingsPersistPostgreSqlDestinationAndPassPreviewReadiness()
    {
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var sourceConnectionId = Guid.NewGuid();
        var destinationConnectionId = Guid.Parse("746e3f51-efe9-45b9-a077-00c8dd2dee2d");
        var pipeline = new PipelineDefinition
        {
            Id = Guid.Parse("d9854822-fa07-4ee2-b73a-1b73cf2069c7"),
            Name = "deneme",
            SourceType = SourceType.MongoDb,
            MongoDbSource = new MongoDbSourceOptions
            {
                SavedConnectionId = sourceConnectionId,
                Database = "etl_demo",
                Collection = "customers"
            },
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Age", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Balance", DataType = SourceFieldType.Decimal },
                new SourceFieldDefinition { Name = "BirthDate", DataType = SourceFieldType.Date },
                new SourceFieldDefinition { Name = "Country", DataType = SourceFieldType.String },
                new SourceFieldDefinition { Name = "CustomerId", DataType = SourceFieldType.String },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String },
                new SourceFieldDefinition { Name = "FullName", DataType = SourceFieldType.String },
                new SourceFieldDefinition { Name = "_id", DataType = SourceFieldType.String }
            ],
            FieldMappings =
            [
                new FieldMapping { SourceField = "Age", TargetField = "Age", IsIncluded = true },
                new FieldMapping { SourceField = "Balance", TargetField = "Balance", IsIncluded = true },
                new FieldMapping { SourceField = "BirthDate", TargetField = "BirthDate", IsIncluded = true },
                new FieldMapping { SourceField = "Country", TargetField = "Country", IsIncluded = true },
                new FieldMapping { SourceField = "CustomerId", TargetField = "CustomerId", IsIncluded = true },
                new FieldMapping { SourceField = "Email", TargetField = "Email", IsIncluded = true },
                new FieldMapping { SourceField = "FullName", TargetField = "FullName", IsIncluded = true },
                new FieldMapping { SourceField = "_id", TargetField = "_id", IsIncluded = true }
            ],
            DestinationType = DestinationType.PostgreSql,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await testDatabase.Repository.AddAsync(pipeline, CancellationToken.None);
        var repository = new CapturingPipelineRepository(testDatabase.Repository);
        var pipelineService = new PipelineService(repository, TimeProvider.System);
        await using var host = await BindingTestHost.StartAsync(
            pipelineService,
            savedConnectionService: new SavedConnectionServiceStub(destinationConnectionId),
            savedMetadataDiscoveryService: new SavedPostgreSqlMetadataDiscoveryStub());
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();

        using var post = await host.Client.PostAsync(
            $"/Pipelines/Edit/{pipeline.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = pipeline.Name,
                ["DestinationType"] = DestinationType.PostgreSql.ToString(),
                ["PostgreSqlDestinationSavedConnectionId"] = destinationConnectionId.ToString(),
                ["PostgreSqlDatabase"] = "etl_demo",
                ["PostgreSqlSchema"] = "etl_demo",
                ["PostgreSqlTable"] = "customer_source",
                ["PostgreSqlColumnMappings[0].OutputField"] = "CustomerId",
                ["PostgreSqlColumnMappings[0].DestinationColumn"] = "customer_id",
                ["PostgreSqlColumnMappings[1].OutputField"] = "FullName",
                ["PostgreSqlColumnMappings[1].DestinationColumn"] = "full_name",
                ["PostgreSqlColumnMappings[2].OutputField"] = "Email",
                ["PostgreSqlColumnMappings[2].DestinationColumn"] = "email",
                ["PostgreSqlColumnMappings[3].OutputField"] = "Country",
                ["PostgreSqlColumnMappings[3].DestinationColumn"] = "country",
                ["PostgreSqlColumnMappings[4].OutputField"] = string.Empty,
                ["PostgreSqlColumnMappings[4].DestinationColumn"] = string.Empty,
                ["PostgreSqlColumnMappings[5].OutputField"] = string.Empty,
                ["PostgreSqlColumnMappings[5].DestinationColumn"] = string.Empty,
                ["PostgreSqlColumnMappings[6].OutputField"] = string.Empty,
                ["PostgreSqlColumnMappings[6].DestinationColumn"] = string.Empty,
                ["PostgreSqlColumnMappings[7].OutputField"] = string.Empty,
                ["PostgreSqlColumnMappings[7].DestinationColumn"] = string.Empty,
                ["PostgreSqlUpsertKeyColumn"] = "customer_id",
                ["__RequestVerificationToken"] = antiForgeryToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var commandDestination = Assert.IsType<PostgreSqlDestinationOptions>(
            repository.UpdatedPipeline!.PostgreSqlDestination);
        AssertPostgreSqlDestination(commandDestination, destinationConnectionId);
        Assert.Equal("CustomerId", repository.UpdatedPipeline.UpsertKeyField);

        var persisted = await testDatabase.Repository.GetByIdAsync(pipeline.Id, CancellationToken.None);
        var persistedDestination = Assert.IsType<PostgreSqlDestinationOptions>(persisted!.PostgreSqlDestination);
        AssertPostgreSqlDestination(persistedDestination, destinationConnectionId);
        Assert.Equal("CustomerId", persisted.UpsertKeyField);

        var readiness = await new PipelineReadinessService(
            repository,
            new FieldMappingService(),
            testDatabase.TargetAccessService).EvaluateAsync(pipeline.Id, CancellationToken.None);

        Assert.True(readiness!.IsReady, string.Join(Environment.NewLine,
            readiness.Problems.Select(problem => $"{problem.Component}: {problem.Message}")));
    }

    [Fact]
    public async Task DeletePost_UsesRouteIdWhenFormContainsConflictingId()
    {
        var routePipeline = CreatePipeline("Route pipeline");
        var postedPipeline = CreatePipeline("Posted pipeline");
        var service = new RecordingPipelineService(routePipeline, postedPipeline);
        await using var host = await BindingTestHost.StartAsync(service);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();

        using var response = await host.Client.PostAsync(
            $"/Pipelines/Delete/{routePipeline.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = postedPipeline.Id.ToString(),
                ["__RequestVerificationToken"] = antiForgeryToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(routePipeline.Id, service.DeletedId);
        Assert.False(service.Contains(routePipeline.Id));
        Assert.True(service.Contains(postedPipeline.Id));
    }

    [Theory]
    [InlineData(TransformationType.SetDefaultValue)]
    [InlineData(TransformationType.FindAndReplace)]
    public async Task TransformationPost_OmittedRequiredValueDoesNotCallRuleService(
        TransformationType type)
    {
        var pipeline = CreatePipeline("Route pipeline");
        var pipelineService = new RecordingPipelineService(pipeline);
        var ruleService = new RecordingTransformationRuleService();
        await using var host = await BindingTestHost.StartAsync(pipelineService, ruleService);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();
        var form = new Dictionary<string, string>
        {
            ["Type"] = type.ToString(),
            ["SourceField"] = "name",
            ["__RequestVerificationToken"] = antiForgeryToken
        };
        if (type == TransformationType.FindAndReplace) form["Find"] = "find";

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Transformations/Create",
            new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(ruleService.LastInput);
    }

    [Theory]
    [InlineData(TransformationType.SetDefaultValue, "DefaultValue")]
    [InlineData(TransformationType.FindAndReplace, "Replace")]
    public async Task TransformationPost_ExplicitEmptyValueRemainsEmpty(
        TransformationType type,
        string postedField)
    {
        var pipeline = CreatePipeline("Route pipeline");
        var pipelineService = new RecordingPipelineService(pipeline);
        var ruleService = new RecordingTransformationRuleService();
        await using var host = await BindingTestHost.StartAsync(pipelineService, ruleService);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();
        var form = new Dictionary<string, string>
        {
            ["Type"] = type.ToString(),
            ["SourceField"] = "name",
            [postedField] = "",
            ["__RequestVerificationToken"] = antiForgeryToken
        };
        if (type == TransformationType.FindAndReplace) form["Find"] = "find";

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Transformations/Create",
            new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var input = Assert.IsType<TransformationRuleInput>(ruleService.LastInput);
        Assert.Equal("", type == TransformationType.SetDefaultValue ? input.DefaultValue : input.Replace);
    }

    [Theory]
    [InlineData(TransformationType.SetDefaultValue, "DefaultValue")]
    [InlineData(TransformationType.FindAndReplace, "Find")]
    [InlineData(TransformationType.FindAndReplace, "Replace")]
    public async Task TransformationPost_WhitespaceValuesRemainUnchanged(
        TransformationType type,
        string postedField)
    {
        var pipeline = CreatePipeline("Route pipeline");
        var ruleService = new RecordingTransformationRuleService();
        await using var host = await BindingTestHost.StartAsync(new RecordingPipelineService(pipeline), ruleService);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();
        var form = new Dictionary<string, string>
        {
            ["Type"] = type.ToString(),
            ["SourceField"] = "name",
            ["__RequestVerificationToken"] = antiForgeryToken
        };
        if (type == TransformationType.FindAndReplace)
        {
            form["Find"] = "find";
            form["Replace"] = "replace";
        }
        form[postedField] = "  ";

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Transformations/Create",
            new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var input = Assert.IsType<TransformationRuleInput>(ruleService.LastInput);
        Assert.Equal("  ", postedField switch
        {
            "DefaultValue" => input.DefaultValue,
            "Find" => input.Find,
            _ => input.Replace
        });
    }

    private static PipelineDefinition CreatePipeline(string name)
    {
        return new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            Name = name
        };
    }

    private static void AssertSelectedOption(string html, string selectId, string value)
    {
        var select = Regex.Match(
            html,
            $"<select\\b(?=[^>]*\\sid=\"{Regex.Escape(selectId)}\")[^>]*>(?<options>.*?)</select>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(select.Success, $"The '{selectId}' selector was not rendered.");
        Assert.Matches(
            $"<option\\b(?=[^>]*\\svalue=\"{Regex.Escape(value)}\")(?=[^>]*\\sselected(?:=|\\s|>))[^>]*>",
            select.Groups["options"].Value);
    }

    private static void AssertPostgreSqlDestination(
        PostgreSqlDestinationOptions destination,
        Guid savedConnectionId)
    {
        Assert.Equal(savedConnectionId, destination.SavedConnectionId);
        Assert.Equal(("etl_demo", "etl_demo", "customer_source"),
            (destination.Database, destination.Schema, destination.Table));
        Assert.Equal("customer_id", destination.UpsertKeyColumn);
        Assert.Equal(
            [
                ("CustomerId", "customer_id"),
                ("FullName", "full_name"),
                ("Email", "email"),
                ("Country", "country")
            ],
            destination.ColumnMappings.Select(mapping =>
                (mapping.OutputField, mapping.DestinationColumn)));
    }

    private sealed class BindingTestHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private BindingTestHost(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<BindingTestHost> StartAsync(
            IPipelineService pipelineService,
            ITransformationRuleService? transformationRuleService = null,
            ISavedDatabaseConnectionService? savedConnectionService = null,
            ISavedConnectionMetadataDiscoveryService? savedMetadataDiscoveryService = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(PipelinesController).Assembly.GetName().Name,
                EnvironmentName = Environments.Development,
                ContentRootPath = FindWebContentRoot()
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services
                .AddControllersWithViews()
                .AddApplicationPart(typeof(PipelinesController).Assembly);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton(pipelineService);
            builder.Services.AddSingleton<ITransformationRuleService>(
                transformationRuleService ?? new RecordingTransformationRuleService());
            if (savedConnectionService is not null)
            {
                builder.Services.AddSingleton(savedConnectionService);
            }
            if (savedMetadataDiscoveryService is not null)
            {
                builder.Services.AddSingleton(savedMetadataDiscoveryService);
            }

            var application = builder.Build();
            application.MapGet(
                "/antiforgery-token",
                (HttpContext context, IAntiforgery antiForgery) =>
                {
                    var tokens = antiForgery.GetAndStoreTokens(context);
                    return Results.Text(tokens.RequestToken!);
                });
            application.MapControllers();
            application.MapControllerRoute(
                name: "default",
                pattern: "{controller}/{action}/{id?}");

            await application.StartAsync();

            var server = application.Services.GetRequiredService<IServer>();
            var address = server.Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer()
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(address)
            };

            return new BindingTestHost(application, client);
        }

        public Task<string> GetAntiForgeryTokenAsync()
        {
            return Client.GetStringAsync("/antiforgery-token");
        }

        private static string FindWebContentRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "EtlTool.Web");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException("The EtlTool.Web content root could not be located.");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class RecordingPipelineService : IPipelineService
    {
        private readonly Dictionary<Guid, PipelineDefinition> _pipelines;

        public RecordingPipelineService(params PipelineDefinition[] pipelines)
        {
            _pipelines = pipelines.ToDictionary(pipeline => pipeline.Id);
        }

        public Guid? UpdatedId { get; private set; }

        public Guid? DeletedId { get; private set; }

        public bool Contains(Guid id)
        {
            return _pipelines.ContainsKey(id);
        }

        public PipelineDefinition GetRequired(Guid id)
        {
            return _pipelines[id];
        }

        public Task<PipelineDefinition> CreateAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<PipelineDefinition?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            _pipelines.TryGetValue(id, out var pipeline);
            return Task.FromResult(pipeline);
        }

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> UpdateAsync(
            Guid id,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            UpdatedId = id;

            if (!_pipelines.ContainsKey(id))
            {
                return Task.FromResult(false);
            }

            _pipelines[id] = pipeline;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            DeletedId = id;
            return Task.FromResult(_pipelines.Remove(id));
        }
    }

    private sealed class RecordingTransformationRuleService : ITransformationRuleService
    {
        public TransformationRuleInput? LastInput { get; private set; }

        public Task<TransformationRule?> CreateAsync(
            Guid pipelineId,
            TransformationRuleInput input,
            CancellationToken cancellationToken)
        {
            LastInput = input;
            return Task.FromResult<TransformationRule?>(new TransformationRule
            {
                Id = Guid.NewGuid(),
                Type = input.Type,
                SourceField = input.SourceField
            });
        }

        public Task<bool> UpdateAsync(Guid pipelineId, Guid ruleId, TransformationRuleInput input, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid pipelineId, Guid ruleId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReorderAsync(
            Guid pipelineId,
            IReadOnlyList<Guid> orderedRuleIds,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class CapturingPipelineRepository(IPipelineDefinitionRepository inner)
        : IPipelineDefinitionRepository
    {
        public PipelineDefinition? UpdatedPipeline { get; private set; }

        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            inner.AddAsync(pipeline, cancellationToken);

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            inner.ListAsync(cancellationToken);

        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken)
        {
            UpdatedPipeline = pipeline;
            return inner.UpdateAsync(pipeline, cancellationToken);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            inner.DeleteAsync(id, cancellationToken);
    }

    private sealed class SavedConnectionServiceStub(Guid postgreSqlConnectionId) : ISavedDatabaseConnectionService
    {
        public Task<SavedDatabaseConnection> CreateAsync(string name, DatabaseProviderType providerType,
            string connectionConfiguration, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SavedDatabaseConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<SavedDatabaseConnection?>(null);

        public Task<IReadOnlyList<SavedDatabaseConnection>> ListAsync(DatabaseProviderType? providerType,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SavedDatabaseConnection>>(
            providerType == DatabaseProviderType.PostgreSql
                ? [new SavedDatabaseConnection
                {
                    Id = postgreSqlConnectionId,
                    Name = "Local PostgreSQL",
                    ProviderType = DatabaseProviderType.PostgreSql,
                    ActiveRevision = 1
                }]
                : []);

        public Task<SavedDatabaseConnection?> UpdateAsync(Guid id, string name, string? replacementConfiguration,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SavedConnectionDeleteResult> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SavedPostgreSqlMetadataDiscoveryStub : ISavedConnectionMetadataDiscoveryService
    {
        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverPostgreSqlDatabasesAsync(Guid connectionId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PostgreSqlDatabaseMetadata>>([new("etl_demo")]);

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverPostgreSqlSchemasAsync(Guid connectionId,
            string database, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PostgreSqlSchemaMetadata>>([new("etl_demo")]);

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverPostgreSqlTablesAsync(Guid connectionId,
            string database, string schema, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PostgreSqlTableMetadata>>(
            [new("mongo_customers"), new("customer_source")]);

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverPostgreSqlColumnsAsync(Guid connectionId,
            string database, string schema, string table, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PostgreSqlColumnMetadata>>(
            [
                new("source_id", "text", false, 1), new("customer_id", "text", false, 2),
                new("email", "text", false, 3), new("customer_name", "text", false, 4),
                new("full_name", "text", false, 5), new("country", "text", false, 6),
                new("balance", "numeric", false, 7)
            ]);

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverPostgreSqlKeyConstraintsAsync(Guid connectionId,
            string database, string schema, string table, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PostgreSqlKeyConstraintMetadata>>(
            [
                new("mongo_customers_source_id_key", PostgreSqlKeyConstraintKind.Unique,
                    [new PostgreSqlKeyColumnMetadata("source_id", 1, false)]),
                new("customer_source_customer_id_key", PostgreSqlKeyConstraintKind.Unique,
                    [new PostgreSqlKeyColumnMetadata("customer_id", 1, false)])
            ]);

        public Task EnsurePostgreSqlDestinationAccessibleAsync(Guid connectionId, string database, string schema,
            string table, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverMongoDatabasesAsync(Guid connectionId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MongoDatabaseMetadata>>([]);

        public Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverMongoCollectionsAsync(Guid connectionId,
            string database, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MongoCollectionMetadata>>([]);

        public Task<IReadOnlyList<SourceFieldDefinition>> InferMongoSchemaAsync(Guid connectionId, string database,
            string collection, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SourceFieldDefinition>>([]);
    }
}
