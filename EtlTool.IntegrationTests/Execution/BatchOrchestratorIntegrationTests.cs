using System.Text;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtlTool.IntegrationTests.Execution;

public sealed class BatchOrchestratorIntegrationTests
{
    [Fact]
    public async Task ExecuteAsync_ComposesCsvProcessingAndConfiguredBatchingThroughDi()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{BatchExecutionOptions.SectionName}:BatchSize"] = "2"
            })
            .Build();
        var options = configuration
            .GetRequiredSection(BatchExecutionOptions.SectionName)
            .Get<BatchExecutionOptions>()!;
        options.Validate();

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IFileExtractor, CsvFileExtractor>();
        services.AddSingleton<IFileExtractorResolver, FileExtractorResolver>();
        services.AddSingleton<IPipelineDefinitionRepository, NullRepository>();
        services.AddSingleton<FieldMappingService>();
        services.AddSingleton<ITransformationHandler, TrimTransformationHandler>();
        services.AddSingleton<TransformationHandlerRegistry>();
        services.AddSingleton<TransformationEngine>();
        services.AddSingleton<IValidationHandler, RequiredValidationHandler>();
        services.AddSingleton<ValidationHandlerRegistry>();
        services.AddSingleton<ValidationEngine>();
        services.AddSingleton<PipelineRowProcessor>();
        services.AddSingleton<IMongoTargetAccessService, AllowedTargetAccessService>();
        services.AddScoped<IPipelineReadinessService, PipelineReadinessService>();
        services.AddScoped<IBatchOrchestrator, BatchOrchestrator>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IBatchOrchestrator>();
        var pipeline = ReadyPipeline();
        var csv = "Id,Name\r\n1, Ada \r\n2, Grace \r\n3, Linus \r\n4, Margaret \r\n5, Barbara \r\n";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var batches = new List<IReadOnlyList<DataRow>>();

        var result = await orchestrator.ExecuteAsync(
            source,
            pipeline,
            (batch, _) =>
            {
                batches.Add(batch);
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal([2, 2, 1], batches.Select(batch => batch.Count));
        Assert.Equal([2L, 3L, 4L, 5L, 6L], batches
            .SelectMany(batch => batch)
            .Select(row => row.SourceRowNumber));
        Assert.Equal(["Ada", "Grace", "Linus", "Margaret", "Barbara"], batches
            .SelectMany(batch => batch)
            .Select(row => row.Values["name"]));
        Assert.Equal(5, result.ProcessedRows);
        Assert.Equal(5, result.ValidRows);
        Assert.Equal(0, result.InvalidRows);
        Assert.Equal(0, result.FilteredRows);
        Assert.Equal(0, result.DeduplicatedRows);
        Assert.True(source.CanRead);
    }

    private static PipelineDefinition ReadyPipeline() => new()
    {
        Id = Guid.NewGuid(),
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id" },
            new SourceFieldDefinition { Name = "Name" }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
            new FieldMapping { SourceField = "Name", TargetField = "name", IsIncluded = true }
        ],
        TransformationRules =
        [
            new TransformationRule
            {
                Id = Guid.NewGuid(),
                Order = 1,
                Type = TransformationType.Trim,
                SourceField = "name"
            }
        ],
        ValidationRules =
        [
            new ValidationRule { Type = ValidationType.Required, Field = "name" }
        ],
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = "id"
    };

    private sealed class NullRepository : IPipelineDefinitionRepository
    {
        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AllowedTargetAccessService : IMongoTargetAccessService
    {
        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnsureUpsertIndexAsync(
            MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
