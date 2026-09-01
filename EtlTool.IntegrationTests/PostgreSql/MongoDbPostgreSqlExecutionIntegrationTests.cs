using System.Data.Common;
using System.Text.Json;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Mapping;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;
using EtlTool.Application.PostgreSql;
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
using EtlTool.Infrastructure.Sources;
using EtlTool.Infrastructure.Uploads;
using EtlTool.IntegrationTests.MongoDB;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace EtlTool.IntegrationTests.PostgreSql;

[CollectionDefinition(CollectionName)]
public sealed class MongoDbPostgreSqlExecutionCollection :
    ICollectionFixture<MongoDbFixture>,
    ICollectionFixture<PostgreSqlFixture>
{
    public const string CollectionName = "MongoDB to PostgreSQL execution";
}

[Collection(MongoDbPostgreSqlExecutionCollection.CollectionName)]
public sealed class MongoDbPostgreSqlExecutionIntegrationTests(
    MongoDbFixture mongoDbFixture,
    PostgreSqlFixture postgreSqlFixture)
{
    [Fact]
    public async Task MongoDbSource_UsesSharedPreviewAndBackgroundExecutionToPostgreSqlIdempotently()
    {
        await using var metadataDatabase = mongoDbFixture.CreateDatabase();
        var sourceDatabase = $"db21_source_{Guid.NewGuid():N}";
        var schema = $"db21_target_{Guid.NewGuid():N}";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"EtlTool-DB21-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        var source = metadataDatabase.Client
            .GetDatabase(sourceDatabase)
            .GetCollection<BsonDocument>("customers");

        try
        {
            await source.InsertManyAsync(
            [
                Customer(1, "Invalid first", "ada@example.test", "-1"),
                Customer(2, " Ada ", "ada@example.test", "10"),
                Customer(3, "Later duplicate", "ada@example.test", "20"),
                Customer(4, "Hidden", "filtered@example.test", "30"),
                Customer(5, "Broken", "broken@example.test", "not-a-number"),
                Customer(6, " Linus ", "linus@example.test", "40")
            ]);
            await ExecuteSqlAsync(connection, $"""
                CREATE SCHEMA {Quote(schema)};
                CREATE TABLE {Quote(schema)}.customers (
                    "Legacy Id" integer NOT NULL,
                    "Customer Name" text NOT NULL,
                    "Email" text PRIMARY KEY,
                    "Balance" integer NOT NULL);
                """);

            var pipeline = Pipeline(sourceDatabase, PostgreSqlDatabaseName(), schema);
            await metadataDatabase.Repository.AddAsync(pipeline, CancellationToken.None);
            var readiness = new PipelineReadinessService(
                metadataDatabase.Repository,
                new FieldMappingService(),
                metadataDatabase.TargetAccessService);
            var mongoOptions = MongoOptions(metadataDatabase);
            var mongoMetadata = new MongoMetadataDatabase(mongoOptions);
            var mongoDiscovery = new MongoSourceMetadataDiscoveryService(
                mongoMetadata,
                metadataDatabase.TargetAccessService);
            var mongoInference = new MongoSourceSchemaInferenceService(
                mongoMetadata,
                mongoDiscovery,
                mongoOptions);
            var processor = Processor();
            var previewFactory = new PreviewSourceFactory(
                new LogicalSourceStore(),
                factory,
                new PostgreSqlMetadataDiscoveryService(factory),
                new PostgreSqlDeterministicOrderingResolver(),
                mongoMetadata,
                mongoOptions,
                mongoInference,
                new SourceSchemaComparisonService());
            var coordinator = new PipelineSourceCommitCoordinator(
                new LogicalSourceStore(),
                previewFactory);

            await using (var previewSnapshot = await coordinator.CapturePreviewAsync(
                pipeline.Id,
                token => metadataDatabase.Repository.GetByIdAsync(pipeline.Id, token),
                readiness.Evaluate,
                CancellationToken.None))
            {
                Assert.Equal(PipelinePreviewSnapshotStatus.Ready, previewSnapshot.Status);
                var preview = await new PreviewService(readiness, processor).PreviewAsync(
                    Assert.IsAssignableFrom<IEtlSource>(previewSnapshot.Source),
                    pipeline,
                    CancellationToken.None);

                Assert.Equal((2, 2, 1, 1), (
                    preview.ValidRowCount,
                    preview.InvalidRowCount,
                    preview.FilteredRowCount,
                    preview.DuplicateRowCount));
                var ada = Assert.Single(preview.FinalValidRows, row =>
                    string.Equals(row.Row.Values["email"] as string, "ada@example.test", StringComparison.Ordinal));
                Assert.Equal(2L, ada.Row.Values["legacy_id"]);
                Assert.Equal("Ada", ada.Row.Values["customer_name"]);
                Assert.Equal(10L, ada.Row.Values["balance"]);
            }

            var queue = new RecordingQueue();
            var admission = new RunAdmissionService(
                new PipelineService(metadataDatabase.Repository, TimeProvider.System),
                readiness,
                coordinator,
                metadataDatabase.EtlRunRepository,
                queue,
                new RunAdmissionOptions(),
                TimeProvider.System);
            var executor = CreateExecutor(
                metadataDatabase,
                readiness,
                factory,
                mongoMetadata,
                mongoOptions,
                mongoInference,
                temporaryRoot);

            var firstAdmission = await admission.AdmitAsync(pipeline.Id, CancellationToken.None);
            Assert.Equal(RunAdmissionStatus.Admitted, firstAdmission.Status);
            await executor.ExecuteAsync(Assert.Single(queue.Jobs), CancellationToken.None);

            var firstRun = Assert.IsType<EtlRun>(await metadataDatabase.EtlRunRepository.GetByIdAsync(
                firstAdmission.RunId!.Value,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, firstRun.Status);
            Assert.Equal((6L, 2L, 2L, 1L, 1L, 2L, 0L), Counters(firstRun));
            Assert.Equal($"error-report-{firstRun.Id:N}.csv", firstRun.ErrorReportPath);

            await using (var report = new LocalErrorReportStore(new ErrorReportStorageOptions
            {
                RootPath = Path.Combine(temporaryRoot, "error-reports")
            }).OpenRead(firstRun))
            {
                Assert.NotNull(report);
                using var reader = new StreamReader(report!);
                var csv = await reader.ReadToEndAsync();
                Assert.Contains("Invalid first", csv, StringComparison.Ordinal);
                Assert.Contains("not-a-number", csv, StringComparison.Ordinal);
            }

            Assert.Equal(
                [
                    (2, "Ada", "ada@example.test", 10),
                    (6, "Linus", "linus@example.test", 40)
                ],
                await TargetRowsAsync(connection, schema));

            var secondAdmission = await admission.AdmitAsync(pipeline.Id, CancellationToken.None);
            Assert.Equal(RunAdmissionStatus.Admitted, secondAdmission.Status);
            await executor.ExecuteAsync(queue.Jobs.Last(), CancellationToken.None);

            var secondRun = Assert.IsType<EtlRun>(await metadataDatabase.EtlRunRepository.GetByIdAsync(
                secondAdmission.RunId!.Value,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, secondRun.Status);
            Assert.Equal((6L, 2L, 2L, 1L, 1L, 0L, 2L), Counters(secondRun));
            Assert.Equal(2, (await TargetRowsAsync(connection, schema)).Count);
        }
        finally
        {
            await ExecuteSqlAsync(connection, $"DROP SCHEMA IF EXISTS {Quote(schema)} CASCADE;");
            await metadataDatabase.Client.DropDatabaseAsync(sourceDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private PostgreSqlConnectionFactory CreatePostgreSqlFactory() => new(new PostgreSqlConnectionOptions
    {
        Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
        {
            ["ReportingDb"] = new() { ConnectionString = postgreSqlFixture.ConnectionString }
        },
        BatchWriteMaximumAttempts = 2,
        BatchWriteRetryDelayMilliseconds = 0
    });

    private string PostgreSqlDatabaseName() =>
        new NpgsqlConnectionStringBuilder(postgreSqlFixture.ConnectionString).Database
        ?? throw new InvalidOperationException("The PostgreSQL fixture did not provide a database name.");

    private static MongoDbOptions MongoOptions(MongoDbTestDatabase database) => new()
    {
        ConnectionString = database.ConnectionString,
        MetadataDatabaseName = database.DatabaseName,
        SourceSchemaSampleDocumentLimit = 100,
        SourceExecutionFetchSize = 1
    };

    private static PipelineRowProcessor Processor() => new(
        new FieldMappingService(),
        new TransformationEngine(new TransformationHandlerRegistry(
        [
            new ConditionalFilterTransformationHandler(),
            new TrimTransformationHandler(),
            new ConvertToIntegerTransformationHandler(),
            new DeduplicateTransformationHandler()
        ])),
        new ValidationEngine(new ValidationHandlerRegistry([new NumericRangeValidationHandler()])));

    private EtlRunBackgroundJobExecutor CreateExecutor(
        MongoDbTestDatabase database,
        PipelineReadinessService readiness,
        PostgreSqlConnectionFactory factory,
        MongoMetadataDatabase mongoMetadata,
        MongoDbOptions mongoOptions,
        MongoSourceSchemaInferenceService mongoInference,
        string temporaryRoot)
    {
        var metadata = new PostgreSqlMetadataDiscoveryService(factory);
        var loader = new PostgreSqlBatchUpsertLoader(factory, metadata, metadata,
            new PostgreSqlConnectionOptions
            {
                Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
                {
                    ["ReportingDb"] = new() { ConnectionString = postgreSqlFixture.ConnectionString }
                },
                BatchWriteMaximumAttempts = 2,
                BatchWriteRetryDelayMilliseconds = 0
            });
        var sourceStore = new RunSourceStore(
            FileStore(temporaryRoot),
            factory,
            metadata,
            new PostgreSqlSourceSchemaConverter(),
            new SourceSchemaComparisonService(),
            new PostgreSqlDeterministicOrderingResolver(),
            mongoMetadata,
            mongoInference,
            mongoOptions);
        var reportStore = new LocalErrorReportStore(new ErrorReportStorageOptions
        {
            RootPath = Path.Combine(temporaryRoot, "error-reports")
        });
        var orchestrator = new BatchOrchestrator(
            readiness,
            Processor(),
            new BatchExecutionOptions { BatchSize = 1 });

        return new EtlRunBackgroundJobExecutor(
            database.EtlRunRepository,
            orchestrator,
            new DataLoaderResolver([loader]),
            TimeProvider.System,
            new CsvErrorReportWriter(),
            sourceStore,
            reportStore,
            NullLogger<EtlRunBackgroundJobExecutor>.Instance);
    }

    private static LocalRunSourceFileStore FileStore(string temporaryRoot)
    {
        var options = new UploadStorageOptions { RootPath = Path.Combine(temporaryRoot, "uploads") };
        return new LocalRunSourceFileStore(
            options,
            new LocalUploadStorage(options),
            new FileExtractorResolver([new CsvFileExtractor(), new XlsxFileExtractor()]));
    }

    private PipelineDefinition Pipeline(string sourceDatabase, string destinationDatabase, string schema) => new()
    {
        Id = Guid.NewGuid(),
        Name = "DB.21 MongoDB to PostgreSQL customers",
        SourceType = SourceType.MongoDb,
        SourceOptions = new SourceOptions { CultureName = "en-US" },
        MongoDbSource = new MongoDbSourceOptions { Database = sourceDatabase, Collection = "customers" },
        ExpectedSchema =
        [
            Field("_id", SourceFieldType.Integer),
            Field("balance", SourceFieldType.String),
            Field("email", SourceFieldType.String),
            Field("fullName", SourceFieldType.String)
        ],
        FieldMappings =
        [
            Mapping("_id", "legacy_id"),
            Mapping("fullName", "customer_name"),
            Mapping("email", "email"),
            Mapping("balance", "balance")
        ],
        TransformationRules =
        [
            Rule(1, TransformationType.FilterRow, "balance",
                ("Operator", FilterOperator.Equals.ToString()), ("Value", "30")),
            Rule(2, TransformationType.Trim, "customer_name"),
            Rule(3, TransformationType.ConvertToInteger, "balance"),
            new TransformationRule
            {
                Id = Guid.NewGuid(),
                Order = 4,
                Type = TransformationType.Deduplicate,
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Fields"] = JsonSerializer.Serialize(new[] { "email" })
                }
            }
        ],
        ValidationRules =
        [
            new ValidationRule
            {
                Id = Guid.NewGuid(),
                Type = ValidationType.NumericRange,
                Field = "balance",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "0" }
            }
        ],
        DestinationType = DestinationType.PostgreSql,
        PostgreSqlDestination = new PostgreSqlDestinationOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = destinationDatabase,
            Schema = schema,
            Table = "customers",
            ColumnMappings =
            [
                new PostgreSqlDestinationColumnMapping { OutputField = "legacy_id", DestinationColumn = "Legacy Id" },
                new PostgreSqlDestinationColumnMapping { OutputField = "customer_name", DestinationColumn = "Customer Name" },
                new PostgreSqlDestinationColumnMapping { OutputField = "email", DestinationColumn = "Email" },
                new PostgreSqlDestinationColumnMapping { OutputField = "balance", DestinationColumn = "Balance" }
            ],
            UpsertKeyColumn = "Email"
        },
        UpsertKeyField = "email"
    };

    private static BsonDocument Customer(int id, string fullName, string email, string balance) => new()
    {
        ["_id"] = id,
        ["fullName"] = fullName,
        ["email"] = email,
        ["balance"] = balance
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
        string field,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type,
        SourceField = field,
        Configuration = configuration.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
    };

    private static async Task<List<(int Id, string Name, string Email, int Balance)>> TargetRowsAsync(
        DbConnection connection,
        string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "Legacy Id", "Customer Name", "Email", "Balance"
            FROM {Quote(schema)}.customers
            ORDER BY "Legacy Id";
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(int, string, string, int)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return rows;
    }

    private static async Task ExecuteSqlAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

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
}
