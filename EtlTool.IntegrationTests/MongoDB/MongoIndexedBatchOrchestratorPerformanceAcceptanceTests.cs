using System.Diagnostics;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
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
using EtlTool.IntegrationTests.Execution;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit.Abstractions;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
[Trait("Category", "Performance")]
public sealed class MongoIndexedBatchOrchestratorPerformanceAcceptanceTests(
    MongoDbFixture fixture,
    ITestOutputHelper output)
{
    private const int DataRowCount = 100_000;
    private const int BatchSize = 1_000;
    private readonly MongoDbFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    [BatchExecutionAcceptanceFact]
    public async Task ExecuteAsync_CreatesMissingUpsertIndexBeforeLoading100kRows()
    {
        await using var database = _fixture.CreateDatabase();
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "performance-100k.csv");
        Assert.True(File.Exists(path), $"The performance CSV was not copied to '{path}'.");
        Assert.Equal(DataRowCount + 1L, File.ReadLines(path).LongCount());

        var targetDatabaseName = $"etl_perf_{Guid.NewGuid():N}";
        var target = new MongoTarget(targetDatabaseName, "customers");
        var targetDatabase = database.Client.GetDatabase(target.DatabaseName);
        var initialCollections = await ListCollectionNamesAsync(targetDatabase);
        var initialSuitableIndexExists = false;
        Assert.DoesNotContain(target.CollectionName, initialCollections);

        var pipeline = CreatePipeline(target);
        var orchestrator = CreateOrchestrator(database.TargetAccessService);
        var batches = 0;
        var stopwatch = Stopwatch.StartNew();
        BatchExecutionResult result;
        await using (var source = new FileEtlSource(
            File.OpenRead(path),
            new CsvFileExtractor(),
            pipeline.SourceOptions))
        {
            result = await orchestrator.ExecuteWithLoadResultAsync(
                source,
                pipeline,
                new CountingLoader(database.Loader, () => batches++),
                static (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                CancellationToken.None);
        }

        stopwatch.Stop();
        var targetCollection = targetDatabase.GetCollection<BsonDocument>(target.CollectionName);
        var finalIndexes = await ListIndexesAsync(targetCollection);
        var finalSuitableIndexExists = finalIndexes.Any(index =>
            index["key"].AsBsonDocument.Equals(new BsonDocument(pipeline.UpsertKeyField, 1))
            && (!index.TryGetValue("collation", out var collation)
                || collation["locale"] == "simple"));

        Assert.False(initialSuitableIndexExists);
        Assert.True(finalSuitableIndexExists);
        Assert.Equal(DataRowCount / BatchSize, batches);
        Assert.Equal(DataRowCount, result.ProcessedRows);
        Assert.Equal(DataRowCount, result.ValidRows);
        Assert.Equal(0, result.InvalidRows);
        Assert.Equal(0, result.FilteredRows);
        Assert.Equal(0, result.DeduplicatedRows);
        Assert.Equal(DataRowCount, result.InsertedRows);
        Assert.Equal(0, result.UpdatedRows);
        Assert.Equal(DataRowCount, await targetCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

        _output.WriteLine($"Source: {DataRowCount:N0} rows, {new FileInfo(path).Length:N0} bytes.");
        _output.WriteLine($"Batch size: {BatchSize:N0}.");
        _output.WriteLine($"Initial suitable index: {initialSuitableIndexExists}.");
        _output.WriteLine($"Final suitable index: {finalSuitableIndexExists}.");
        _output.WriteLine($"Elapsed: {stopwatch.Elapsed.TotalSeconds:F3} seconds.");
        _output.WriteLine("Terminal status: Completed.");
        _output.WriteLine(
            $"Counters: processed={result.ProcessedRows}, valid={result.ValidRows}, " +
            $"invalid={result.InvalidRows}, filtered={result.FilteredRows}, " +
            $"duplicate={result.DeduplicatedRows}, inserted={result.InsertedRows}, " +
            $"updated={result.UpdatedRows}.");
    }

    private static BatchOrchestrator CreateOrchestrator(
        IMongoTargetAccessService targetAccessService)
    {
        var mapping = new FieldMappingService();
        return new BatchOrchestrator(
            new PipelineReadinessService(
                new NullRepository(),
                mapping,
                targetAccessService),
            new PipelineRowProcessor(
                mapping,
                new TransformationEngine(new TransformationHandlerRegistry(
                [
                    new TrimTransformationHandler(),
                    new ConvertToIntegerTransformationHandler(),
                    new ConvertToDecimalTransformationHandler(),
                    new ConvertToDateTransformationHandler()
                ])),
                new ValidationEngine(new ValidationHandlerRegistry(
                [
                    new RequiredValidationHandler(),
                    new EmailValidationHandler(),
                    new NumericRangeValidationHandler(),
                    new DateRangeValidationHandler()
                ]))),
            new BatchExecutionOptions { BatchSize = BatchSize });
    }

    private sealed class CountingLoader(IDataLoader inner, Action onLoad) : IDataLoader
    {
        public DestinationType DestinationType => inner.DestinationType;

        public Task PrepareAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken) =>
            inner.PrepareAsync(pipeline, cancellationToken);

        public Task<BatchLoadResult> UpsertBatchAsync(
            IReadOnlyList<DataRow> rows,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            onLoad();
            return inner.UpsertBatchAsync(rows, pipeline, cancellationToken);
        }
    }

    private static PipelineDefinition CreatePipeline(MongoTarget target) => new()
    {
        Id = Guid.NewGuid(),
        Name = "100K indexed MongoDB performance acceptance",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            CultureName = "en-US",
            DateFormat = "yyyy-MM-dd",
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema =
        [
            Field("CustomerId", SourceFieldType.Integer),
            Field("FullName", SourceFieldType.String),
            Field("Email", SourceFieldType.String),
            Field("Age", SourceFieldType.Integer),
            Field("Balance", SourceFieldType.Decimal),
            Field("BirthDate", SourceFieldType.Date),
            Field("Country", SourceFieldType.String)
        ],
        FieldMappings =
        [
            Mapping("CustomerId", "customerId"),
            Mapping("FullName", "fullName"),
            Mapping("Email", "email"),
            Mapping("Age", "age"),
            Mapping("Balance", "balance"),
            Mapping("BirthDate", "birthDate"),
            Mapping("Country", "country")
        ],
        TransformationRules =
        [
            Rule(1, TransformationType.Trim, "fullName"),
            Rule(2, TransformationType.ConvertToInteger, "age"),
            Rule(3, TransformationType.ConvertToDecimal, "balance"),
            Rule(4, TransformationType.ConvertToDate, "birthDate")
        ],
        ValidationRules =
        [
            Validation(ValidationType.Required, "customerId"),
            Validation(ValidationType.EmailFormat, "email"),
            Validation(ValidationType.NumericRange, "age", ("Minimum", "0"), ("Maximum", "150")),
            Validation(ValidationType.DateRange, "birthDate", ("Minimum", "1900-01-01"), ("Maximum", "2100-01-01"))
        ],
        DestinationDatabase = target.DatabaseName,
        DestinationCollection = target.CollectionName,
        UpsertKeyField = "customerId"
    };

    private static SourceFieldDefinition Field(string name, SourceFieldType type) => new()
    {
        Name = name,
        DataType = type
    };

    private static FieldMapping Mapping(string source, string target) => new()
    {
        SourceField = source,
        TargetField = target,
        IsIncluded = true
    };

    private static TransformationRule Rule(
        int order,
        TransformationType type,
        string sourceField) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type,
        SourceField = sourceField
    };

    private static ValidationRule Validation(
        ValidationType type,
        string field,
        params (string Key, string Value)[] configuration) => new()
    {
        Type = type,
        Field = field,
        Configuration = configuration.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal)
    };

    private static async Task<List<string>> ListCollectionNamesAsync(IMongoDatabase database)
    {
        using var cursor = await database.ListCollectionNamesAsync();
        return await cursor.ToListAsync();
    }

    private static async Task<List<BsonDocument>> ListIndexesAsync(
        IMongoCollection<BsonDocument> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

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
}
