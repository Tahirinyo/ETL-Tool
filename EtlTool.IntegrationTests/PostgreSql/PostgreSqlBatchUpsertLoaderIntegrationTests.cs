using System.Data.Common;
using System.Runtime.CompilerServices;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Mapping;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
using Npgsql;

namespace EtlTool.IntegrationTests.PostgreSql;

[Collection(PostgreSqlTestCollection.CollectionName)]
public sealed class PostgreSqlBatchUpsertLoaderIntegrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task UpsertBatchAsync_ReportsInsertUpdateAndMixedBranchesWithExplicitMappingsAndTypes()
    {
        var schema = Name("db19_loader");
        var factory = CreateFactory();
        var loader = CreateLoader(factory);
        var pipeline = Pipeline(schema, "Customer Rows");

        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(connection, $"""
                CREATE SCHEMA {Quote(schema)};
                CREATE TABLE {Quote(schema)}.{Quote("Customer Rows")} (
                    "Customer Id" text PRIMARY KEY,
                    "Full Name" text NOT NULL,
                    "Credit Score" numeric(12,2),
                    "Is Active" boolean,
                    "Occurred Local" timestamp without time zone);
                """);
            await loader.PrepareAsync(pipeline, CancellationToken.None);

            var occurred = new DateTime(2026, 8, 31, 10, 30, 0, DateTimeKind.Unspecified);
            var first = await loader.UpsertBatchAsync(
                [
                    Row("one", "Ada", 10.25m, true, occurred),
                    Row("two", "Grace", 20.50m, false, occurred.AddDays(1))
                ],
                pipeline,
                CancellationToken.None);
            var identical = await loader.UpsertBatchAsync(
                [
                    Row("one", "Ada", 10.25m, true, occurred),
                    Row("two", "Grace", 20.50m, false, occurred.AddDays(1))
                ],
                pipeline,
                CancellationToken.None);
            var changed = await loader.UpsertBatchAsync(
                [
                    Row("one", "Ada Lovelace", 11.75m, false, occurred.AddHours(1)),
                    Row("two", "Grace Hopper", 21.00m, true, occurred.AddDays(2))
                ],
                pipeline,
                CancellationToken.None);
            var mixed = await loader.UpsertBatchAsync(
                [
                    Row("two", "Rear Admiral Hopper", 22.00m, true, occurred.AddDays(3)),
                    Row("three", "Linus", 30.00m, true, occurred.AddDays(4))
                ],
                pipeline,
                CancellationToken.None);

            Assert.Equal((2L, 0L), (first.InsertedRows, first.UpdatedRows));
            Assert.Equal((0L, 2L), (identical.InsertedRows, identical.UpdatedRows));
            Assert.Equal((0L, 2L), (changed.InsertedRows, changed.UpdatedRows));
            Assert.Equal((1L, 1L), (mixed.InsertedRows, mixed.UpdatedRows));
            Assert.All([first, identical, changed, mixed], result =>
                Assert.True(result.InsertedRows + result.UpdatedRows <= 2));

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT "Customer Id", "Full Name", "Credit Score", "Is Active", "Occurred Local"
                FROM {Quote(schema)}.{Quote("Customer Rows")}
                ORDER BY "Customer Id";
                """;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string Id, string Name, decimal Score, bool Active, DateTime Occurred)>();
            while (await reader.ReadAsync())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetDecimal(2),
                    reader.GetBoolean(3),
                    reader.GetDateTime(4)));
            }

            Assert.Equal(3, rows.Count);
            Assert.Equal(("one", "Ada Lovelace", 11.75m, false, occurred.AddHours(1)), rows[0]);
            Assert.Equal(("three", "Linus", 30.00m, true, occurred.AddDays(4)), rows[1]);
            Assert.Equal(("two", "Rear Admiral Hopper", 22.00m, true, occurred.AddDays(3)), rows[2]);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {Quote(schema)} CASCADE;");
        }
    }

    [Fact]
    public async Task UpsertBatchAsync_RejectsEmptyKeyAndRollsBackWholeBatchOnConstraintFailure()
    {
        var schema = Name("db19_atomic");
        var factory = CreateFactory();
        var loader = CreateLoader(factory);
        var pipeline = Pipeline(schema, "customers");

        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(connection, $"""
                CREATE SCHEMA {Quote(schema)};
                CREATE TABLE {Quote(schema)}.customers (
                    "Customer Id" text PRIMARY KEY,
                    "Full Name" text NOT NULL UNIQUE,
                    "Credit Score" numeric(12,2),
                    "Is Active" boolean,
                    "Occurred Local" timestamp without time zone);
                """);
            await loader.PrepareAsync(pipeline, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => loader.UpsertBatchAsync(
                [Row(" ", "Empty", 1m, true, DateTime.UnixEpoch)],
                pipeline,
                CancellationToken.None));
            var failure = await Assert.ThrowsAsync<BatchLoadException>(() => loader.UpsertBatchAsync(
                [
                    Row("one", "Duplicate", 1m, true, DateTime.UnixEpoch),
                    Row("two", "Duplicate", 2m, false, DateTime.UnixEpoch)
                ],
                pipeline,
                CancellationToken.None));

            Assert.False(failure.ConfirmedResult.HasCommittedRows);
            Assert.Equal(0L, await ScalarLongAsync(
                connection,
                $"SELECT count(*) FROM {Quote(schema)}.customers;"));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {Quote(schema)} CASCADE;");
        }
    }

    [Fact]
    public async Task Orchestrator_PreservesCommittedBatchWhenLaterPostgreSqlBatchFails()
    {
        var schema = Name("db19_partial");
        var factory = CreateFactory();
        var loader = CreateLoader(factory);
        var pipeline = Pipeline(schema, "customers");
        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id" },
            new SourceFieldDefinition { Name = "Name" },
            new SourceFieldDefinition { Name = "Score" },
            new SourceFieldDefinition { Name = "Active" },
            new SourceFieldDefinition { Name = "Occurred" }
        ];
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
            new FieldMapping { SourceField = "Name", TargetField = "display_name", IsIncluded = true },
            new FieldMapping { SourceField = "Score", TargetField = "score", IsIncluded = true },
            new FieldMapping { SourceField = "Active", TargetField = "active", IsIncluded = true },
            new FieldMapping { SourceField = "Occurred", TargetField = "occurred", IsIncluded = true }
        ];

        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(connection, $"""
                CREATE SCHEMA {Quote(schema)};
                CREATE TABLE {Quote(schema)}.customers (
                    "Customer Id" text PRIMARY KEY,
                    "Full Name" text NOT NULL,
                    "Credit Score" integer,
                    "Is Active" boolean,
                    "Occurred Local" timestamp without time zone);
                """);
            var processor = new PipelineRowProcessor(
                new FieldMappingService(),
                new TransformationEngine(new TransformationHandlerRegistry([])),
                new ValidationEngine(new ValidationHandlerRegistry([])));
            var orchestrator = new BatchOrchestrator(
                new AlwaysReady(),
                processor,
                new BatchExecutionOptions { BatchSize = 1 });
            await using var source = new MemorySource(
                SourceRow("one", "Ada", 10),
                SourceRow("two", "Grace", "not-an-integer"));

            var exception = await Assert.ThrowsAsync<BatchExecutionException>(() =>
                orchestrator.ExecuteWithLoadResultAsync(
                    source,
                    pipeline,
                    loader,
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.CompletedTask,
                    CancellationToken.None));

            Assert.Equal((1L, 0L), (
                exception.ConfirmedProgress.InsertedRows,
                exception.ConfirmedProgress.UpdatedRows));
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                $"SELECT count(*) FROM {Quote(schema)}.customers;"));
            Assert.Equal("one", await ScalarStringAsync(
                connection,
                $"SELECT \"Customer Id\" FROM {Quote(schema)}.customers;"));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {Quote(schema)} CASCADE;");
        }
    }

    [Fact]
    public async Task Orchestrator_DeduplicatesSourceRowsAndRejectsEmptyKeysBeforePostgreSqlPersistence()
    {
        var schema = Name("db20_source_keys");
        var factory = CreateFactory();
        var loader = CreateLoader(factory);
        var pipeline = Pipeline(schema, "customers");
        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id" },
            new SourceFieldDefinition { Name = "Name" },
            new SourceFieldDefinition { Name = "Score" },
            new SourceFieldDefinition { Name = "Active" },
            new SourceFieldDefinition { Name = "Occurred" }
        ];

        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(connection, $"""
                CREATE SCHEMA {Quote(schema)};
                CREATE TABLE {Quote(schema)}.customers (
                    "Customer Id" text PRIMARY KEY,
                    "Full Name" text NOT NULL,
                    "Credit Score" integer,
                    "Is Active" boolean,
                    "Occurred Local" timestamp without time zone);
                """);
            var processor = new PipelineRowProcessor(
                new FieldMappingService(),
                new TransformationEngine(new TransformationHandlerRegistry([])),
                new ValidationEngine(new ValidationHandlerRegistry([])));
            var orchestrator = new BatchOrchestrator(
                new AlwaysReady(),
                processor,
                new BatchExecutionOptions { BatchSize = 2 });
            await using var source = new MemorySource(
                SourceRow("one", "First wins", 1),
                SourceRow("one", "Later duplicate", 2),
                SourceRow(null, "Null key", 3),
                SourceRow(string.Empty, "Empty key", 4),
                SourceRow(" ", "Whitespace key", 5),
                SourceRow("two", "Second valid", 6));

            var result = await orchestrator.ExecuteWithLoadResultAsync(
                source,
                pipeline,
                loader,
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal((6L, 2L, 3L, 1L), (
                result.ProcessedRows,
                result.ValidRows,
                result.InvalidRows,
                result.DeduplicatedRows));
            Assert.Equal((2L, 0L), (result.InsertedRows, result.UpdatedRows));
            Assert.Equal(2L, await ScalarLongAsync(
                connection,
                $"SELECT count(*) FROM {Quote(schema)}.customers;"));
            Assert.Equal("First wins", await ScalarStringAsync(
                connection,
                $"SELECT \"Full Name\" FROM {Quote(schema)}.customers WHERE \"Customer Id\" = 'one';"));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {Quote(schema)} CASCADE;");
        }
    }

    private PostgreSqlConnectionFactory CreateFactory() => new(
        new PostgreSqlConnectionOptions
        {
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
            {
                ["ReportingDb"] = new() { ConnectionString = fixture.ConnectionString }
            },
            BatchWriteMaximumAttempts = 2,
            BatchWriteRetryDelayMilliseconds = 0
        });

    private static PostgreSqlBatchUpsertLoader CreateLoader(PostgreSqlConnectionFactory factory)
    {
        var metadata = new PostgreSqlMetadataDiscoveryService(factory);
        return new PostgreSqlBatchUpsertLoader(
            factory,
            metadata,
            metadata,
            new PostgreSqlConnectionOptions
            {
                BatchWriteMaximumAttempts = 2,
                BatchWriteRetryDelayMilliseconds = 0
            });
    }

    private PipelineDefinition Pipeline(string schema, string table) => new()
    {
        DestinationType = DestinationType.PostgreSql,
        FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
            new FieldMapping { SourceField = "Name", TargetField = "display_name", IsIncluded = true },
            new FieldMapping { SourceField = "Score", TargetField = "score", IsIncluded = true },
            new FieldMapping { SourceField = "Active", TargetField = "active", IsIncluded = true },
            new FieldMapping { SourceField = "Occurred", TargetField = "occurred", IsIncluded = true }
        ],
        UpsertKeyField = "id",
        PostgreSqlDestination = new PostgreSqlDestinationOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database
                ?? throw new InvalidOperationException("The PostgreSQL fixture did not provide a database."),
            Schema = schema,
            Table = table,
            ColumnMappings =
            [
                new PostgreSqlDestinationColumnMapping { OutputField = "id", DestinationColumn = "Customer Id" },
                new PostgreSqlDestinationColumnMapping { OutputField = "display_name", DestinationColumn = "Full Name" },
                new PostgreSqlDestinationColumnMapping { OutputField = "score", DestinationColumn = "Credit Score" },
                new PostgreSqlDestinationColumnMapping { OutputField = "active", DestinationColumn = "Is Active" },
                new PostgreSqlDestinationColumnMapping { OutputField = "occurred", DestinationColumn = "Occurred Local" }
            ],
            UpsertKeyColumn = "Customer Id"
        }
    };

    private static DataRow Row(
        string id,
        string name,
        decimal score,
        bool active,
        DateTime occurred)
    {
        var row = new DataRow { SourceRowNumber = 2 };
        row.Values["id"] = id;
        row.Values["display_name"] = name;
        row.Values["score"] = score;
        row.Values["active"] = active;
        row.Values["occurred"] = occurred;
        return row;
    }

    private static DataRow SourceRow(string? id, string name, object score)
    {
        var row = new DataRow { SourceRowNumber = 2 };
        row.Values["Id"] = id;
        row.Values["Name"] = name;
        row.Values["Score"] = score;
        row.Values["Active"] = true;
        row.Values["Occurred"] = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified);
        return row;
    }

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task ExecuteAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private sealed class AlwaysReady : IPipelineReadinessService
    {
        public PipelineReadinessResult Evaluate(PipelineDefinition pipeline) => new([]);

        public Task<PipelineReadinessResult?> EvaluateAsync(
            Guid pipelineId,
            CancellationToken cancellationToken) => Task.FromResult<PipelineReadinessResult?>(new([]));
    }

    private sealed class MemorySource(params DataRow[] rows) : IEtlSource
    {
        public async IAsyncEnumerable<DataRow> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
