using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Processing;
using EtlTool.Application.Reporting;
using EtlTool.Application.Sources;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Execution;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.Reporting;
using EtlTool.Infrastructure.Uploads;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoDbExecutionIntegrationTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task ExecuteAsync_AdmitsCapturedMongoSourceAndStreamsThroughExistingEngine()
    {
        await using var database = fixture.CreateDatabase();
        var sourceDatabaseName = $"db15_src_{Guid.NewGuid():N}";
        var targetDatabaseName = $"db15_dst_{Guid.NewGuid():N}";
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"EtlTool-DB15-{Guid.NewGuid():N}");
        var sourceCollection = database.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("customers");

        try
        {
            await sourceCollection.InsertManyAsync(
            [
                new BsonDocument
                {
                    ["_id"] = 2,
                    ["logical_id"] = "B",
                    ["name"] = "Linus",
                    ["amount"] = Decimal128.Parse("20.5")
                },
                new BsonDocument
                {
                    ["_id"] = 1,
                    ["logical_id"] = "A",
                    ["name"] = "Ada",
                    ["amount"] = Decimal128.Parse("10")
                }
            ]);
            var pipeline = Pipeline(sourceDatabaseName, targetDatabaseName);
            await database.Repository.AddAsync(pipeline, CancellationToken.None);
            var readiness = new PipelineReadinessService(
                database.Repository,
                new FieldMappingService(),
                database.TargetAccessService);
            var queue = new RecordingQueue();
            var admission = new RunAdmissionService(
                new PipelineService(database.Repository, TimeProvider.System),
                readiness,
                new PipelineSourceCommitCoordinator(new LogicalSourceStore()),
                database.EtlRunRepository,
                queue,
                new RunAdmissionOptions(),
                TimeProvider.System);

            var admitted = await admission.AdmitAsync(pipeline.Id, CancellationToken.None);

            Assert.Equal(RunAdmissionStatus.Admitted, admitted.Status);
            var run = Assert.IsType<EtlRun>(await database.EtlRunRepository.GetByIdAsync(
                admitted.RunId!.Value,
                CancellationToken.None));
            Assert.Equal(string.Empty, run.OriginalFileName);
            Assert.Equal(string.Empty, run.StoredFilePath);
            Assert.Equal(sourceDatabaseName, run.ExecutionConfiguration!.MongoDbSource!.Database);
            Assert.Equal("customers", run.ExecutionConfiguration.MongoDbSource.Collection);
            Assert.DoesNotContain(
                run.ExecutionConfiguration.GetType().GetProperties(),
                property => property.Name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));

            await CreateExecutor(database, readiness, temporaryRoot)
                .ExecuteAsync(Assert.Single(queue.Jobs), CancellationToken.None);

            var completed = Assert.IsType<EtlRun>(await database.EtlRunRepository.GetByIdAsync(
                run.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, completed.Status);
            Assert.Equal((2L, 2L, 0L, 0L, 0L, 2L, 0L), Counters(completed));
            var target = database.Client
                .GetDatabase(targetDatabaseName)
                .GetCollection<BsonDocument>("loaded_customers");
            var documents = await target
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("id"))
                .ToListAsync();
            Assert.Equal(["A", "B"], documents.Select(document => document["id"].AsString));
            Assert.Equal([10m, 20.5m], documents.Select(document => document["amount"].AsDecimal));
        }
        finally
        {
            await database.Client.DropDatabaseAsync(sourceDatabaseName);
            await database.Client.DropDatabaseAsync(targetDatabaseName);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_LateSchemaDriftAfterInferenceSamplePreservesCommittedBatch()
    {
        await using var database = fixture.CreateDatabase();
        var sourceDatabaseName = $"db15_lsrc_{Guid.NewGuid():N}";
        var targetDatabaseName = $"db15_ldst_{Guid.NewGuid():N}";
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"EtlTool-DB15-Late-{Guid.NewGuid():N}");
        var sourceCollection = database.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("customers");

        try
        {
            await sourceCollection.InsertManyAsync(
            [
                new BsonDocument
                {
                    ["_id"] = 1,
                    ["logical_id"] = "A",
                    ["name"] = "Ada",
                    ["amount"] = Decimal128.Parse("10")
                },
                new BsonDocument
                {
                    ["_id"] = 2,
                    ["logical_id"] = "B",
                    ["name"] = "Linus",
                    ["amount"] = 20,
                    ["unexpected"] = "late drift"
                }
            ]);
            var pipeline = Pipeline(sourceDatabaseName, targetDatabaseName);
            await database.Repository.AddAsync(pipeline, CancellationToken.None);
            var run = new EtlRun
            {
                Id = Guid.NewGuid(),
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                Status = EtlRunStatus.Queued,
                ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(pipeline)
            };
            await database.EtlRunRepository.AddAsync(run, CancellationToken.None);
            var readiness = new PipelineReadinessService(
                database.Repository,
                new FieldMappingService(),
                database.TargetAccessService);

            var exception = await Assert.ThrowsAsync<MongoSourceSchemaChangedException>(() =>
                CreateExecutor(
                        database,
                        readiness,
                        temporaryRoot,
                        sourceSchemaSampleDocumentLimit: 1)
                    .ExecuteAsync(new BackgroundJob(run.Id), CancellationToken.None));

            Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
            var partial = Assert.IsType<EtlRun>(await database.EtlRunRepository.GetByIdAsync(
                run.Id,
                CancellationToken.None));
            Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, partial.SystemError);
            Assert.Equal(
                (EtlRunStatus.PartiallyCompleted, 1L, 1L, 0L, 0L, 0L, 1L, 0L),
                (partial.Status, partial.ProcessedRows, partial.ValidRows, partial.InvalidRows,
                    partial.FilteredRows, partial.DeduplicatedRows, partial.InsertedRows, partial.UpdatedRows));
            var target = database.Client
                .GetDatabase(targetDatabaseName)
                .GetCollection<BsonDocument>("loaded_customers");
            var loaded = await target.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
            Assert.Equal("A", loaded["id"].AsString);
        }
        finally
        {
            await database.Client.DropDatabaseAsync(sourceDatabaseName);
            await database.Client.DropDatabaseAsync(targetDatabaseName);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static EtlRunBackgroundJobExecutor CreateExecutor(
        MongoDbTestDatabase database,
        PipelineReadinessService readiness,
        string temporaryRoot,
        int sourceSchemaSampleDocumentLimit = 100)
    {
        var mongoOptions = new MongoDbOptions
        {
            ConnectionString = database.ConnectionString,
            MetadataDatabaseName = database.DatabaseName,
            SourceSchemaSampleDocumentLimit = sourceSchemaSampleDocumentLimit,
            SourceExecutionFetchSize = 1
        };
        var metadata = new MongoMetadataDatabase(mongoOptions);
        var discovery = new MongoSourceMetadataDiscoveryService(
            metadata,
            database.TargetAccessService);
        var sourceStore = new RunSourceStore(
            CreateFileStore(temporaryRoot),
            new PostgreSqlConnectionFactory(new PostgreSqlConnectionOptions()),
            new UnusedPostgreSqlMetadataDiscoveryService(),
            new PostgreSqlSourceSchemaConverter(),
            new SourceSchemaComparisonService(),
            new PostgreSqlDeterministicOrderingResolver(),
            metadata,
            new MongoSourceSchemaInferenceService(metadata, discovery, mongoOptions),
            mongoOptions);
        var fieldMapping = new FieldMappingService();
        var orchestrator = new BatchOrchestrator(
            readiness,
            new PipelineRowProcessor(
                fieldMapping,
                new TransformationEngine(new TransformationHandlerRegistry([])),
                new ValidationEngine(new ValidationHandlerRegistry([]))),
            new BatchExecutionOptions { BatchSize = 1 });
        var reportStore = new LocalErrorReportStore(new ErrorReportStorageOptions
        {
            RootPath = Path.Combine(temporaryRoot, "error-reports")
        });

        return new EtlRunBackgroundJobExecutor(
            database.EtlRunRepository,
            orchestrator,
            new DataLoaderResolver([database.Loader]),
            TimeProvider.System,
            new CsvErrorReportWriter(),
            sourceStore,
            reportStore,
            NullLogger<EtlRunBackgroundJobExecutor>.Instance);
    }

    private static LocalRunSourceFileStore CreateFileStore(string temporaryRoot)
    {
        var options = new UploadStorageOptions
        {
            RootPath = Path.Combine(temporaryRoot, "uploads")
        };
        return new LocalRunSourceFileStore(
            options,
            new LocalUploadStorage(options),
            new FileExtractorResolver([new CsvFileExtractor(), new XlsxFileExtractor()]));
    }

    private static PipelineDefinition Pipeline(string sourceDatabase, string targetDatabase) => new()
    {
        Id = Guid.NewGuid(),
        Name = "MongoDB customers",
        SourceType = SourceType.MongoDb,
        SourceOptions = new SourceOptions(),
        MongoDbSource = new MongoDbSourceOptions
        {
            Database = sourceDatabase,
            Collection = "customers"
        },
        ExpectedSchema =
        [
            Field("_id", SourceFieldType.Integer),
            Field("amount", SourceFieldType.Decimal),
            Field("logical_id", SourceFieldType.String),
            Field("name", SourceFieldType.String)
        ],
        FieldMappings =
        [
            Mapping("_id", "source_id"),
            Mapping("amount", "amount"),
            Mapping("logical_id", "id"),
            Mapping("name", "name")
        ],
        DestinationDatabase = targetDatabase,
        DestinationCollection = "loaded_customers",
        UpsertKeyField = "id"
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

    private static (long, long, long, long, long, long, long) Counters(EtlRun run) =>
        (run.ProcessedRows, run.ValidRows, run.InvalidRows, run.FilteredRows,
            run.DeduplicatedRows, run.InsertedRows, run.UpdatedRows);

    private sealed class RecordingQueue : IBackgroundJobQueue
    {
        public List<BackgroundJob> Jobs { get; } = [];

        public ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Jobs.Add(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LogicalSourceStore : IWizardSourceStore
    {
        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPostgreSqlMetadataDiscoveryService :
        EtlTool.Application.PostgreSql.IPostgreSqlMetadataDiscoveryService
    {
        public Task<IReadOnlyList<EtlTool.Application.PostgreSql.PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(
            string connectionProfile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EtlTool.Application.PostgreSql.PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EtlTool.Application.PostgreSql.PostgreSqlTableMetadata>> DiscoverTablesAsync(
            string connectionProfile,
            string database,
            string schema,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EtlTool.Application.PostgreSql.PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EtlTool.Application.PostgreSql.PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
