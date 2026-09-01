using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Processing;
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
using EtlTool.IntegrationTests.Execution;
using EtlTool.IntegrationTests.MongoDB;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Xunit.Abstractions;

namespace EtlTool.IntegrationTests.PostgreSql;

[CollectionDefinition(CollectionName)]
public sealed class PostgreSqlMongoExecutionCollection :
    ICollectionFixture<PostgreSqlFixture>,
    ICollectionFixture<MongoDbFixture>
{
    public const string CollectionName = "PostgreSQL to MongoDB execution";
}

[Collection(PostgreSqlMongoExecutionCollection.CollectionName)]
public sealed class PostgreSqlMongoExecutionIntegrationTests(
    PostgreSqlFixture postgreSqlFixture,
    MongoDbFixture mongoDbFixture,
    ITestOutputHelper output)
{
    [Fact]
    public async Task ExecuteAsync_StreamsSharedProcessingIntoMongoAndRerunsIdempotently()
    {
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var schema = $"db11_{Guid.NewGuid():N}";
        var table = "execution_rows";
        var targetDatabase = $"{testDatabase.DatabaseName}_target";
        var targetCollection = "customers";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"EtlTool-DB11-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);

        try
        {
            await ExecuteSqlAsync(
                setupConnection,
                $"CREATE SCHEMA \"{schema}\"; " +
                $"CREATE TABLE \"{schema}\".\"{table}\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Kind\" text NOT NULL, " +
                "\"Logical Id\" text NOT NULL, \"Name\" text NOT NULL, \"Amount\" text NOT NULL); " +
                $"INSERT INTO \"{schema}\".\"{table}\" VALUES " +
                "(1, 'normal', 'A', 'Invalid first', '-1'), " +
                "(2, 'normal', 'A', ' Ada ', '10'), " +
                "(3, 'normal', 'A', 'Later duplicate', '20'), " +
                "(4, 'filtered', 'C', 'Hidden', '30'), " +
                "(5, 'normal', 'D', 'Broken', 'not-a-number'), " +
                "(6, 'normal', 'B', ' Linus ', '40');");

            var pipeline = Pipeline(
                GetPostgreSqlDatabaseName(),
                schema,
                table,
                targetDatabase,
                targetCollection);
            var executor = CreateExecutor(
                testDatabase,
                factory,
                temporaryRoot,
                batchSize: 1);

            var firstRun = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(firstRun, CancellationToken.None);
            await executor.ExecuteAsync(new BackgroundJob(firstRun.Id), CancellationToken.None);

            var completed = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                firstRun.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, completed.Status);
            Assert.Equal((6L, 2L, 2L, 1L, 1L, 2L, 0L), Counters(completed));
            Assert.Equal($"error-report-{firstRun.Id:N}.csv", completed.ErrorReportPath);

            var target = testDatabase.Client
                .GetDatabase(targetDatabase)
                .GetCollection<BsonDocument>(targetCollection);
            var documents = await target
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("logical_id"))
                .ToListAsync();
            Assert.Equal(2, documents.Count);
            Assert.Equal(["A", "B"], documents.Select(document => document["logical_id"].AsString));
            Assert.Equal(["Ada", "Linus"], documents.Select(document => document["name"].AsString));
            Assert.Equal([10L, 40L], documents.Select(document => document["amount"].AsInt64));

            var reportStore = CreateErrorReportStore(temporaryRoot);
            await using (var report = reportStore.OpenRead(completed))
            {
                Assert.NotNull(report);
                using var reader = new StreamReader(report!);
                var csv = await reader.ReadToEndAsync();
                Assert.Contains("Validation", csv, StringComparison.Ordinal);
                Assert.Contains("Transformation", csv, StringComparison.Ordinal);
                Assert.Contains("Invalid first", csv, StringComparison.Ordinal);
                Assert.Contains("not-a-number", csv, StringComparison.Ordinal);
            }

            var secondRun = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(secondRun, CancellationToken.None);
            await executor.ExecuteAsync(new BackgroundJob(secondRun.Id), CancellationToken.None);

            var rerun = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                secondRun.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, rerun.Status);
            Assert.Equal((6L, 2L, 2L, 1L, 1L, 0L, 2L), Counters(rerun));
            Assert.Equal(2, await target.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await ExecuteSqlAsync(setupConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await testDatabase.Client.DropDatabaseAsync(targetDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_PostgreSqlDateUsesSharedDateProcessingBsonAndUpsertSemantics()
    {
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var schema = $"db11_date_{Guid.NewGuid():N}";
        var table = "dated_rows";
        var targetDatabase = $"{testDatabase.DatabaseName}_target";
        var targetCollection = "events";
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"EtlTool-DB11-Date-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);

        try
        {
            await ExecuteSqlAsync(
                setupConnection,
                $"CREATE SCHEMA \"{schema}\"; " +
                $"CREATE TABLE \"{schema}\".\"{table}\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Event Date\" date NOT NULL, " +
                "\"Display Date\" date NOT NULL, \"Raw Date\" date NOT NULL, " +
                "\"Label\" text NOT NULL, \"Is Active\" boolean NOT NULL); " +
                $"INSERT INTO \"{schema}\".\"{table}\" VALUES " +
                "(1, DATE '2026-06-15', DATE '2026-06-15', DATE '2026-06-15', 'in-range', TRUE), " +
                "(2, DATE '2025-12-31', DATE '2025-12-31', DATE '2025-12-31', 'out-of-range', FALSE);");
            var pipeline = DatePipeline(
                GetPostgreSqlDatabaseName(),
                schema,
                table,
                targetDatabase,
                targetCollection);
            var executor = CreateExecutor(
                testDatabase,
                factory,
                temporaryRoot,
                batchSize: 1);

            var firstRun = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(firstRun, CancellationToken.None);
            await executor.ExecuteAsync(new BackgroundJob(firstRun.Id), CancellationToken.None);

            var completed = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                firstRun.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, completed.Status);
            Assert.Equal((2L, 1L, 1L, 0L, 0L, 1L, 0L), Counters(completed));
            var target = testDatabase.Client
                .GetDatabase(targetDatabase)
                .GetCollection<BsonDocument>(targetCollection);
            var document = await target.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
            var canonicalDate = new DateTime(2026, 6, 15);
            var expectedBsonDate = new BsonDateTime(
                DateTime.SpecifyKind(canonicalDate, DateTimeKind.Utc));

            Assert.Equal(BsonType.DateTime, document["event_date"].BsonType);
            Assert.Equal(
                expectedBsonDate.MillisecondsSinceEpoch,
                document["event_date"].AsBsonDateTime.MillisecondsSinceEpoch);
            Assert.Equal(
                expectedBsonDate.MillisecondsSinceEpoch,
                document["raw_date"].AsBsonDateTime.MillisecondsSinceEpoch);
            Assert.Equal(
                canonicalDate.ToString(null, CultureInfo.InvariantCulture),
                document["display_date"].AsString);
            Assert.Equal(1, document["source_id"].AsInt32);
            Assert.Equal("in-range", document["label"].AsString);
            Assert.True(document["is_active"].AsBoolean);

            var secondRun = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(secondRun, CancellationToken.None);
            await executor.ExecuteAsync(new BackgroundJob(secondRun.Id), CancellationToken.None);

            var rerun = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                secondRun.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, rerun.Status);
            Assert.Equal((2L, 1L, 1L, 0L, 0L, 0L, 1L), Counters(rerun));
            Assert.Equal(1, await target.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await ExecuteSqlAsync(setupConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await testDatabase.Client.DropDatabaseAsync(targetDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("type-change")]
    [InlineData("removed-column")]
    [InlineData("added-column")]
    public async Task ExecuteAsync_AdmittedRunFailsBeforeMongoWritesWhenLiveSchemaDrifts(
        string schemaMutation)
    {
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var schema = $"db11_schema_drift_{Guid.NewGuid():N}";
        var table = "execution_rows";
        var targetDatabase = $"{testDatabase.DatabaseName}_target";
        var targetCollection = "customers";
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"EtlTool-DB11-SchemaDrift-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);

        try
        {
            await ExecuteSqlAsync(
                setupConnection,
                $"CREATE SCHEMA \"{schema}\"; " +
                $"CREATE TABLE \"{schema}\".\"{table}\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Kind\" text NOT NULL, " +
                "\"Logical Id\" text NOT NULL, \"Name\" text NOT NULL, \"Amount\" text NOT NULL); " +
                $"INSERT INTO \"{schema}\".\"{table}\" VALUES (1, 'normal', 'A', 'Ada', '10');");
            var pipeline = Pipeline(
                GetPostgreSqlDatabaseName(),
                schema,
                table,
                targetDatabase,
                targetCollection);
            var admittedRun = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(admittedRun, CancellationToken.None);

            var mutationSql = schemaMutation switch
            {
                "type-change" =>
                    $"ALTER TABLE \"{schema}\".\"{table}\" " +
                    "ALTER COLUMN \"Source Id\" TYPE text USING \"Source Id\"::text;",
                "removed-column" =>
                    $"ALTER TABLE \"{schema}\".\"{table}\" DROP COLUMN \"Amount\";",
                "added-column" =>
                    $"ALTER TABLE \"{schema}\".\"{table}\" ADD COLUMN \"Added\" text;",
                _ => throw new InvalidOperationException("Unknown schema mutation test case.")
            };
            await ExecuteSqlAsync(setupConnection, mutationSql);
            var executor = CreateExecutor(
                testDatabase,
                factory,
                temporaryRoot,
                batchSize: 1);

            var exception = await Assert.ThrowsAsync<PostgreSqlSourceSchemaChangedException>(() =>
                executor.ExecuteAsync(
                    new BackgroundJob(admittedRun.Id),
                    CancellationToken.None));

            Assert.Equal(PostgreSqlSourceSchemaChangedException.SafeMessage, exception.Message);
            var failed = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                admittedRun.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Failed, failed.Status);
            Assert.Equal(PostgreSqlSourceSchemaChangedException.SafeMessage, failed.SystemError);
            Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L), Counters(failed));
            Assert.Equal(0, failed.TotalRows);
            Assert.Null(failed.ErrorReportPath);
            var target = testDatabase.Client
                .GetDatabase(targetDatabase)
                .GetCollection<BsonDocument>(targetCollection);
            Assert.Equal(0, await target.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await ExecuteSqlAsync(setupConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await testDatabase.Client.DropDatabaseAsync(targetDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoDeterministicKeyFailsRunBeforeMongoWrites()
    {
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var schema = $"db11_no_key_{Guid.NewGuid():N}";
        var table = "unordered_rows";
        var targetDatabase = $"{testDatabase.DatabaseName}_target";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"EtlTool-DB11-NoKey-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);

        try
        {
            await ExecuteSqlAsync(
                setupConnection,
                $"CREATE SCHEMA \"{schema}\"; " +
                $"CREATE TABLE \"{schema}\".\"{table}\" " +
                "(\"Source Id\" integer NOT NULL, \"Kind\" text NOT NULL, " +
                "\"Logical Id\" text NOT NULL, \"Name\" text NOT NULL, \"Amount\" text NOT NULL); " +
                $"INSERT INTO \"{schema}\".\"{table}\" VALUES (1, 'normal', 'A', 'Ada', '10');");
            var pipeline = Pipeline(
                GetPostgreSqlDatabaseName(),
                schema,
                table,
                targetDatabase,
                "customers");
            var run = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
            var executor = CreateExecutor(testDatabase, factory, temporaryRoot, batchSize: 1);

            await Assert.ThrowsAsync<PostgreSqlDeterministicOrderingUnavailableException>(() =>
                executor.ExecuteAsync(new BackgroundJob(run.Id), CancellationToken.None));

            var failed = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                run.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Failed, failed.Status);
            Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L), Counters(failed));
            var target = testDatabase.Client
                .GetDatabase(targetDatabase)
                .GetCollection<BsonDocument>("customers");
            Assert.Equal(0, await target.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await ExecuteSqlAsync(setupConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await testDatabase.Client.DropDatabaseAsync(targetDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancellationStopsPostgreSqlRunBeforeFurtherMongoWrites()
    {
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var schema = $"db11_cancel_{Guid.NewGuid():N}";
        var table = "cancellation_rows";
        var targetDatabase = $"{testDatabase.DatabaseName}_target";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"EtlTool-DB11-Cancel-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);

        try
        {
            await ExecuteSqlAsync(
                setupConnection,
                $"CREATE SCHEMA \"{schema}\"; " +
                $"CREATE TABLE \"{schema}\".\"{table}\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Kind\" text NOT NULL, " +
                "\"Logical Id\" text NOT NULL, \"Name\" text NOT NULL, \"Amount\" text NOT NULL); " +
                $"INSERT INTO \"{schema}\".\"{table}\" VALUES " +
                "(1, 'normal', 'A', 'Ada', '10'), " +
                "(2, 'normal', 'B', 'Grace', '20'), " +
                "(3, 'normal', 'C', 'Linus', '30');");
            var pipeline = Pipeline(
                GetPostgreSqlDatabaseName(),
                schema,
                table,
                targetDatabase,
                "customers");
            var run = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
            var loader = new BlockingAfterFirstLoadLoader(testDatabase.Loader);
            var executor = CreateExecutor(
                testDatabase,
                factory,
                temporaryRoot,
                batchSize: 1,
                loader: loader);
            using var cancellation = new CancellationTokenSource();

            var execution = executor.ExecuteAsync(new BackgroundJob(run.Id), cancellation.Token);
            await loader.SecondCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            var interrupted = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                run.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Interrupted, interrupted.Status);
            Assert.Equal(1, interrupted.InsertedRows);
            Assert.Equal(0, interrupted.UpdatedRows);
            var target = testDatabase.Client
                .GetDatabase(targetDatabase)
                .GetCollection<BsonDocument>("customers");
            Assert.Equal(1, await target.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await ExecuteSqlAsync(setupConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await testDatabase.Client.DropDatabaseAsync(targetDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_SourceFailureAfterCommittedBatchIsPartiallyCompletedAndDisposesSource()
    {
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var schema = $"db11_partial_{Guid.NewGuid():N}";
        var table = "partial_rows";
        var targetDatabase = $"{testDatabase.DatabaseName}_target";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"EtlTool-DB11-Partial-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);

        try
        {
            await ExecuteSqlAsync(
                setupConnection,
                $"CREATE SCHEMA \"{schema}\"; " +
                $"CREATE TABLE \"{schema}\".\"{table}\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Kind\" text NOT NULL, " +
                "\"Logical Id\" text NOT NULL, \"Name\" text NOT NULL, \"Amount\" text NOT NULL); " +
                $"INSERT INTO \"{schema}\".\"{table}\" VALUES " +
                "(1, 'normal', 'A', 'Ada', '10'), " +
                "(2, 'normal', 'B', 'Grace', '20');");
            var pipeline = Pipeline(
                GetPostgreSqlDatabaseName(),
                schema,
                table,
                targetDatabase,
                "customers");
            var run = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
            var sourceStore = new FailingAfterFirstRowRunSourceStore(
                CreateRunSourceStore(factory, temporaryRoot));
            var executor = CreateExecutor(
                testDatabase,
                factory,
                temporaryRoot,
                batchSize: 1,
                sourceStore: sourceStore);

            await Assert.ThrowsAsync<IOException>(() =>
                executor.ExecuteAsync(new BackgroundJob(run.Id), CancellationToken.None));

            var partial = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                run.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.PartiallyCompleted, partial.Status);
            Assert.Equal((1L, 1L, 0L, 0L, 0L, 1L, 0L), Counters(partial));
            Assert.Null(partial.ErrorReportPath);
            Assert.True(sourceStore.SourceDisposed);
            Assert.True(sourceStore.ReleaseCalled);
            var target = testDatabase.Client
                .GetDatabase(targetDatabase)
                .GetCollection<BsonDocument>("customers");
            Assert.Equal(1, await target.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await ExecuteSqlAsync(setupConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await testDatabase.Client.DropDatabaseAsync(targetDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [BatchExecutionAcceptanceFact]
    [Trait("Category", "Performance")]
    public async Task ExecuteAsync_Streams100kPostgreSqlRowsIntoMongoInConfiguredBatches()
    {
        const int sourceRowCount = 100_000;
        const int batchSize = 1_000;
        await using var testDatabase = mongoDbFixture.CreateDatabase();
        var schema = $"db12_performance_{Guid.NewGuid():N}";
        var table = "performance_rows";
        var targetDatabase = $"{testDatabase.DatabaseName}_target";
        var targetCollection = "customers";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"EtlTool-DB12-Performance-{Guid.NewGuid():N}");
        var factory = CreatePostgreSqlFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        var snapshots = new List<MemorySnapshot>();

        try
        {
            await ExecuteSqlAsync(
                setupConnection,
                $"CREATE SCHEMA \"{schema}\"; " +
                $"CREATE TABLE \"{schema}\".\"{table}\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Kind\" text NOT NULL, " +
                "\"Logical Id\" text NOT NULL, \"Name\" text NOT NULL, \"Amount\" text NOT NULL); " +
                $"INSERT INTO \"{schema}\".\"{table}\" " +
                "SELECT value, 'normal', 'customer-' || value, ' Customer ' || value || ' ', '1' " +
                $"FROM generate_series(1, {sourceRowCount}) AS value;");

            var pipeline = Pipeline(
                GetPostgreSqlDatabaseName(),
                schema,
                table,
                targetDatabase,
                targetCollection);
            var sourceStore = new TrackingRunSourceStore(CreateRunSourceStore(factory, temporaryRoot));
            ForceFullCollection();
            snapshots.Add(CaptureSnapshot("Baseline", 0));
            var stopwatch = Stopwatch.StartNew();
            var loader = new TrackingLoader(
                testDatabase.Loader,
                sourceStore,
                (batchNumber, rowsRead) => snapshots.Add(CaptureSnapshot(
                    $"Batch{batchNumber}",
                    rowsRead)));
            var executor = CreateExecutor(
                testDatabase,
                factory,
                temporaryRoot,
                batchSize,
                loader,
                sourceStore);
            var run = Run(pipeline);
            await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);

            await executor.ExecuteAsync(new BackgroundJob(run.Id), CancellationToken.None);
            stopwatch.Stop();
            ForceFullCollection();
            snapshots.Add(CaptureSnapshot("PostRun", sourceStore.RowsRead));

            var completed = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository.GetByIdAsync(
                run.Id,
                CancellationToken.None));
            Assert.Equal(EtlRunStatus.Completed, completed.Status);
            Assert.Equal(
                (100_000L, 100_000L, 0L, 0L, 0L, 100_000L, 0L),
                Counters(completed));
            Assert.Equal(sourceRowCount / batchSize, loader.BatchCount);
            Assert.Equal(batchSize, loader.FirstBatchSize);
            Assert.True(loader.SourceRowsReadAtFirstLoad > 0);
            Assert.True(
                loader.SourceRowsReadAtFirstLoad < sourceRowCount,
                "The first MongoDB batch was not written until all PostgreSQL source rows were enumerated.");
            var target = testDatabase.Client
                .GetDatabase(targetDatabase)
                .GetCollection<BsonDocument>(targetCollection);
            Assert.Equal(sourceRowCount, await target.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

            WritePerformanceMeasurements(
                sourceRowCount,
                batchSize,
                stopwatch.Elapsed,
                loader.SourceRowsReadAtFirstLoad,
                snapshots);
        }
        finally
        {
            await ExecuteSqlAsync(setupConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await testDatabase.Client.DropDatabaseAsync(targetDatabase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private PostgreSqlConnectionFactory CreatePostgreSqlFactory() => new(
        new PostgreSqlConnectionOptions
        {
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
            {
                ["ReportingDb"] = new() { ConnectionString = postgreSqlFixture.ConnectionString }
            }
        });

    private string GetPostgreSqlDatabaseName() =>
        new NpgsqlConnectionStringBuilder(postgreSqlFixture.ConnectionString).Database
        ?? throw new InvalidOperationException("The PostgreSQL test container did not provide a database name.");

    private static PipelineDefinition Pipeline(
        string sourceDatabase,
        string sourceSchema,
        string sourceTable,
        string targetDatabase,
        string targetCollection) => new()
    {
        Id = Guid.NewGuid(),
        Name = "DB.11 PostgreSQL execution",
        SourceType = SourceType.PostgreSql,
        SourceOptions = new SourceOptions { CultureName = "en-US" },
        PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = sourceDatabase,
            Schema = sourceSchema,
            Table = sourceTable
        },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Source Id", DataType = SourceFieldType.Integer },
            new SourceFieldDefinition { Name = "Kind", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "Logical Id", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "Name", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "Amount", DataType = SourceFieldType.String }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Source Id", TargetField = "source_id" },
            new FieldMapping { SourceField = "Kind", TargetField = "kind" },
            new FieldMapping { SourceField = "Logical Id", TargetField = "logical_id" },
            new FieldMapping { SourceField = "Name", TargetField = "name" },
            new FieldMapping { SourceField = "Amount", TargetField = "amount" }
        ],
        TransformationRules =
        [
            Rule(
                1,
                TransformationType.FilterRow,
                "kind",
                ("Operator", FilterOperator.Equals.ToString()),
                ("Value", "filtered")),
            Rule(2, TransformationType.Trim, "name"),
            Rule(3, TransformationType.ConvertToInteger, "amount"),
            new TransformationRule
            {
                Id = Guid.NewGuid(),
                Order = 4,
                Type = TransformationType.Deduplicate,
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Fields"] = JsonSerializer.Serialize(new[] { "logical_id" })
                }
            }
        ],
        ValidationRules =
        [
            new ValidationRule
            {
                Id = Guid.NewGuid(),
                Type = ValidationType.NumericRange,
                Field = "amount",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Minimum"] = "0"
                }
            }
        ],
        DestinationDatabase = targetDatabase,
        DestinationCollection = targetCollection,
        UpsertKeyField = "logical_id"
    };

    private static PipelineDefinition DatePipeline(
        string sourceDatabase,
        string sourceSchema,
        string sourceTable,
        string targetDatabase,
        string targetCollection) => new()
    {
        Id = Guid.NewGuid(),
        Name = "DB.11 PostgreSQL date execution",
        SourceType = SourceType.PostgreSql,
        SourceOptions = new SourceOptions
        {
            CultureName = "en-US",
            DateFormat = "yyyy-MM-dd"
        },
        PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = sourceDatabase,
            Schema = sourceSchema,
            Table = sourceTable
        },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Source Id", DataType = SourceFieldType.Integer },
            new SourceFieldDefinition { Name = "Event Date", DataType = SourceFieldType.Date },
            new SourceFieldDefinition { Name = "Display Date", DataType = SourceFieldType.Date },
            new SourceFieldDefinition { Name = "Raw Date", DataType = SourceFieldType.Date },
            new SourceFieldDefinition { Name = "Label", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "Is Active", DataType = SourceFieldType.Boolean }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Source Id", TargetField = "source_id" },
            new FieldMapping { SourceField = "Event Date", TargetField = "event_date" },
            new FieldMapping { SourceField = "Display Date", TargetField = "display_date" },
            new FieldMapping { SourceField = "Raw Date", TargetField = "raw_date" },
            new FieldMapping { SourceField = "Label", TargetField = "label" },
            new FieldMapping { SourceField = "Is Active", TargetField = "is_active" }
        ],
        TransformationRules =
        [
            Rule(1, TransformationType.ConvertToString, "display_date")
        ],
        ValidationRules =
        [
            new ValidationRule
            {
                Id = Guid.NewGuid(),
                Type = ValidationType.DateRange,
                Field = "event_date",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Minimum"] = "2026-01-01",
                    ["Maximum"] = "2026-12-31"
                }
            }
        ],
        DestinationDatabase = targetDatabase,
        DestinationCollection = targetCollection,
        UpsertKeyField = "event_date"
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
        Configuration = configuration.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal)
    };

    private static EtlRun Run(PipelineDefinition pipeline) => new()
    {
        Id = Guid.NewGuid(),
        PipelineId = pipeline.Id,
        PipelineName = pipeline.Name,
        Status = EtlRunStatus.Queued,
        ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(pipeline)
    };

    private static EtlRunBackgroundJobExecutor CreateExecutor(
        MongoDbTestDatabase testDatabase,
        PostgreSqlConnectionFactory postgreSqlFactory,
        string temporaryRoot,
        int batchSize,
        IDataLoader? loader = null,
        IRunSourceStore? sourceStore = null)
    {
        var fieldMappingService = new FieldMappingService();
        var readiness = new PipelineReadinessService(
            testDatabase.Repository,
            fieldMappingService,
            testDatabase.TargetAccessService);
        var processor = new PipelineRowProcessor(
            fieldMappingService,
            new TransformationEngine(new TransformationHandlerRegistry(
            [
                new ConditionalFilterTransformationHandler(),
                new TrimTransformationHandler(),
                new ConvertToIntegerTransformationHandler(),
                new ConvertToStringTransformationHandler(),
                new DeduplicateTransformationHandler()
            ])),
            new ValidationEngine(new ValidationHandlerRegistry(
            [
                new NumericRangeValidationHandler(),
                new DateRangeValidationHandler()
            ])));
        var orchestrator = new BatchOrchestrator(
            readiness,
            processor,
            testDatabase.TargetAccessService,
            new BatchExecutionOptions { BatchSize = batchSize });
        var errorReportStore = CreateErrorReportStore(temporaryRoot);

        return new EtlRunBackgroundJobExecutor(
            testDatabase.EtlRunRepository,
            orchestrator,
            loader ?? testDatabase.Loader,
            TimeProvider.System,
            new CsvErrorReportWriter(),
            sourceStore ?? CreateRunSourceStore(postgreSqlFactory, temporaryRoot),
            errorReportStore,
            NullLogger<EtlRunBackgroundJobExecutor>.Instance);
    }

    private static RunSourceStore CreateRunSourceStore(
        PostgreSqlConnectionFactory postgreSqlFactory,
        string temporaryRoot)
    {
        var uploadRoot = Path.Combine(temporaryRoot, "uploads");
        var uploadOptions = new UploadStorageOptions { RootPath = uploadRoot };
        var fileStore = new LocalRunSourceFileStore(
            uploadOptions,
            new LocalUploadStorage(uploadOptions),
            new FileExtractorResolver([new CsvFileExtractor(), new XlsxFileExtractor()]));
        var mongoOptions = new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_postgresql_execution_tests"
        };
        var mongoMetadata = new MongoMetadataDatabase(mongoOptions);
        var mongoTargetAccess = new MongoTargetAccessService(mongoMetadata, mongoOptions);
        return new RunSourceStore(
            fileStore,
            postgreSqlFactory,
            new PostgreSqlMetadataDiscoveryService(postgreSqlFactory),
            new PostgreSqlSourceSchemaConverter(),
            new SourceSchemaComparisonService(),
            new PostgreSqlDeterministicOrderingResolver(),
            mongoMetadata,
            new MongoSourceSchemaInferenceService(
                mongoMetadata,
                new MongoSourceMetadataDiscoveryService(mongoMetadata, mongoTargetAccess),
                mongoOptions),
            mongoOptions);
    }

    private static LocalErrorReportStore CreateErrorReportStore(string temporaryRoot) => new(
        new ErrorReportStorageOptions
        {
            RootPath = Path.Combine(temporaryRoot, "error-reports")
        });

    private static (long, long, long, long, long, long, long) Counters(EtlRun run) =>
        (run.ProcessedRows, run.ValidRows, run.InvalidRows, run.FilteredRows,
            run.DeduplicatedRows, run.InsertedRows, run.UpdatedRows);

    private static async Task ExecuteSqlAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private void WritePerformanceMeasurements(
        int sourceRowCount,
        int batchSize,
        TimeSpan elapsed,
        long sourceRowsReadAtFirstLoad,
        IReadOnlyList<MemorySnapshot> snapshots)
    {
        var baseline = snapshots[0];
        var peak = snapshots.MaxBy(snapshot => snapshot.PrivateMemoryBytes)!;
        var postRun = snapshots[^1];
        output.WriteLine($"Source: PostgreSQL {sourceRowCount:N0} rows.");
        output.WriteLine($"Batch size: {batchSize:N0}.");
        output.WriteLine($"First MongoDB batch after {sourceRowsReadAtFirstLoad:N0}/{sourceRowCount:N0} source rows.");
        output.WriteLine($"Elapsed: {elapsed.TotalMilliseconds:F0} ms.");
        output.WriteLine("Snapshot           Rows  ManagedMiB  WorkingSetMiB  PrivateMiB");
        foreach (var snapshot in new[] { baseline, peak, postRun }.Distinct())
        {
            output.WriteLine(
                $"{snapshot.Name,-16} {snapshot.RowsRead,6:N0} " +
                $"{ToMiB(snapshot.ManagedBytes),11:F2} {ToMiB(snapshot.WorkingSetBytes),14:F2} {ToMiB(snapshot.PrivateMemoryBytes),11:F2}");
        }
    }

    private static MemorySnapshot CaptureSnapshot(string name, long rowsRead)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemorySnapshot(
            name,
            rowsRead,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            process.PrivateMemorySize64);
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static double ToMiB(long bytes) => bytes / 1024d / 1024d;

    private sealed class BlockingAfterFirstLoadLoader(IDataLoader inner) : IDataLoader
    {
        private int _callCount;

        public TaskCompletionSource SecondCallEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BatchLoadResult> UpsertBatchAsync(
            IReadOnlyList<DataRow> rows,
            MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                return await inner.UpsertBatchAsync(
                    rows,
                    target,
                    upsertKeyField,
                    cancellationToken);
            }

            SecondCallEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocked load unexpectedly resumed.");
        }
    }

    private sealed class TrackingLoader(
        IDataLoader inner,
        TrackingRunSourceStore sourceStore,
        Action<int, long> onBatchStarting) : IDataLoader
    {
        private int _batchCount;

        public int BatchCount => _batchCount;

        public int FirstBatchSize { get; private set; }

        public long SourceRowsReadAtFirstLoad { get; private set; }

        public async Task<BatchLoadResult> UpsertBatchAsync(
            IReadOnlyList<DataRow> rows,
            MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken)
        {
            var batchNumber = Interlocked.Increment(ref _batchCount);
            var rowsRead = sourceStore.RowsRead;
            if (batchNumber == 1)
            {
                FirstBatchSize = rows.Count;
                SourceRowsReadAtFirstLoad = rowsRead;
            }

            onBatchStarting(batchNumber, rowsRead);
            return await inner.UpsertBatchAsync(rows, target, upsertKeyField, cancellationToken);
        }
    }

    private sealed class TrackingRunSourceStore(IRunSourceStore inner) : IRunSourceStore
    {
        private TrackingSource? _source;

        public long RowsRead => _source?.RowsRead ?? 0;

        public async Task<IEtlSource> OpenAsync(EtlRun run, CancellationToken cancellationToken)
        {
            _source = new TrackingSource(await inner.OpenAsync(run, cancellationToken));
            return _source;
        }

        public Task ReleaseAsync(EtlRun run, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(run, cancellationToken);

        private sealed class TrackingSource(IEtlSource innerSource) : IEtlSource
        {
            private long _rowsRead;

            public long RowsRead => Interlocked.Read(ref _rowsRead);

            public async IAsyncEnumerable<DataRow> ReadAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var row in innerSource
                    .ReadAsync(cancellationToken)
                    .WithCancellation(cancellationToken))
                {
                    Interlocked.Increment(ref _rowsRead);
                    yield return row;
                }
            }

            public ValueTask DisposeAsync() => innerSource.DisposeAsync();
        }
    }

    private sealed record MemorySnapshot(
        string Name,
        long RowsRead,
        long ManagedBytes,
        long WorkingSetBytes,
        long PrivateMemoryBytes);

    private sealed class FailingAfterFirstRowRunSourceStore(IRunSourceStore inner) : IRunSourceStore
    {
        public bool SourceDisposed { get; private set; }

        public bool ReleaseCalled { get; private set; }

        public async Task<IEtlSource> OpenAsync(EtlRun run, CancellationToken cancellationToken) =>
            new FailingAfterFirstRowSource(
                await inner.OpenAsync(run, cancellationToken),
                () => SourceDisposed = true);

        public async Task ReleaseAsync(EtlRun run, CancellationToken cancellationToken)
        {
            ReleaseCalled = true;
            await inner.ReleaseAsync(run, cancellationToken);
        }

        private sealed class FailingAfterFirstRowSource(
            IEtlSource innerSource,
            Action onDispose) : IEtlSource
        {
            public async IAsyncEnumerable<DataRow> ReadAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var row in innerSource
                    .ReadAsync(cancellationToken)
                    .WithCancellation(cancellationToken))
                {
                    yield return row;
                    throw new IOException("Controlled PostgreSQL source failure after the first row.");
                }
            }

            public async ValueTask DisposeAsync()
            {
                await innerSource.DisposeAsync();
                onDispose();
            }
        }
    }
}
