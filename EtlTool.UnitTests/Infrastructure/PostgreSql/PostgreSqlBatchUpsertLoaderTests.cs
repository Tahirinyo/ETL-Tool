using System.Data.Common;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;

namespace EtlTool.UnitTests.Infrastructure.PostgreSql;

public sealed class PostgreSqlBatchUpsertLoaderTests
{
    [Fact]
    public async Task UpsertBatchAsync_RetriesTransientDefinitelyUncommittedFailureAndCountsOnce()
    {
        var executor = new SequencedExecutor(
            Failure(isTransient: true, isDefinitelyUncommitted: true),
            new BatchLoadResult(2, 0));
        var loader = CreateLoader(executor, maximumAttempts: 3);

        var result = await loader.UpsertBatchAsync(
            [Row("one", "Ada"), Row("two", "Grace")],
            Pipeline(),
            CancellationToken.None);

        Assert.Equal((2L, 0L), (result.InsertedRows, result.UpdatedRows));
        Assert.Equal(2, executor.Attempts);
    }

    [Fact]
    public async Task UpsertBatchAsync_StopsAtRetryLimitWithoutFalseConfirmedProgress()
    {
        var executor = new SequencedExecutor(
            Failure(isTransient: true, isDefinitelyUncommitted: true));
        var loader = CreateLoader(executor, maximumAttempts: 3);

        var exception = await Assert.ThrowsAsync<BatchLoadException>(() => loader.UpsertBatchAsync(
            [Row("one", "Ada")],
            Pipeline(),
            CancellationToken.None));

        Assert.Equal(3, executor.Attempts);
        Assert.Same(BatchLoadResult.Empty, exception.ConfirmedResult);
        Assert.DoesNotContain("simulated provider detail", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UpsertBatchAsync_DoesNotRetryNonTransientOrCommitAmbiguousFailure(
        bool isTransient,
        bool isDefinitelyUncommitted)
    {
        var executor = new SequencedExecutor(Failure(isTransient, isDefinitelyUncommitted));
        var loader = CreateLoader(executor, maximumAttempts: 3);

        await Assert.ThrowsAsync<BatchLoadException>(() => loader.UpsertBatchAsync(
            [Row("one", "Ada")],
            Pipeline(),
            CancellationToken.None));

        Assert.Equal(1, executor.Attempts);
    }

    [Fact]
    public async Task UpsertBatchAsync_CancellationStopsRetryAndIsNotWrapped()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new SequencedExecutor((_, _) =>
        {
            cancellation.Cancel();
            throw Failure(isTransient: true, isDefinitelyUncommitted: true);
        });
        var loader = CreateLoader(executor, maximumAttempts: 3, retryDelayMilliseconds: 10_000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.UpsertBatchAsync(
            [Row("one", "Ada")],
            Pipeline(),
            cancellation.Token));

        Assert.Equal(1, executor.Attempts);
    }

    [Fact]
    public async Task UpsertBatchAsync_RejectsEmptyOrMissingMappedValuesBeforePersistence()
    {
        var executor = new SequencedExecutor(new BatchLoadResult(1, 0));
        var loader = CreateLoader(executor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.UpsertBatchAsync(
            [Row(" ", "Ada")],
            Pipeline(),
            CancellationToken.None));
        var missing = Row("one", "Ada");
        missing.Values.Remove("display_name");
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.UpsertBatchAsync(
            [missing],
            Pipeline(),
            CancellationToken.None));

        Assert.Equal(0, executor.Attempts);
    }

    [Fact]
    public async Task PrepareAsync_ValidatesLiveColumnsAndEligibleUpsertConstraint()
    {
        var metadata = new MetadataDiscovery(
            [
                new PostgreSqlColumnMetadata("customer_id", "text", false, 1),
                new PostgreSqlColumnMetadata("full_name", "text", true, 2)
            ],
            [new PostgreSqlKeyConstraintMetadata(
                "customers_pkey",
                PostgreSqlKeyConstraintKind.PrimaryKey,
                [new PostgreSqlKeyColumnMetadata("customer_id", 1, false)])]);
        var access = new DestinationAccess();
        var loader = CreateLoader(new SequencedExecutor(BatchLoadResult.Empty), metadata: metadata, access: access);

        await loader.PrepareAsync(Pipeline(), CancellationToken.None);

        Assert.Equal(1, access.Calls);
        Assert.Equal(1, metadata.ColumnCalls);
        Assert.Equal(1, metadata.ConstraintCalls);

        metadata.Constraints = [];
        var exception = await Assert.ThrowsAsync<PostgreSqlDestinationPreparationException>(
            () => loader.PrepareAsync(Pipeline(), CancellationToken.None));
        Assert.Equal(PostgreSqlDestinationPreparationException.SafeMessage, exception.Message);
    }

    [Fact]
    public void Options_RejectInvalidBatchRetryConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PostgreSqlConnectionOptions { BatchWriteMaximumAttempts = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new PostgreSqlConnectionOptions { BatchWriteRetryDelayMilliseconds = -1 }.Validate());
    }

    private static PostgreSqlBatchUpsertLoader CreateLoader(
        IPostgreSqlBatchExecutor executor,
        int maximumAttempts = 2,
        int retryDelayMilliseconds = 0,
        MetadataDiscovery? metadata = null,
        DestinationAccess? access = null) => new(
            new UnusedConnectionFactory(),
            metadata ?? new MetadataDiscovery([], []),
            access ?? new DestinationAccess(),
            new PostgreSqlConnectionOptions
            {
                BatchWriteMaximumAttempts = maximumAttempts,
                BatchWriteRetryDelayMilliseconds = retryDelayMilliseconds
            },
            executor);

    private static PipelineDefinition Pipeline() => new()
    {
        DestinationType = DestinationType.PostgreSql,
        FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
            new FieldMapping { SourceField = "Name", TargetField = "display_name", IsIncluded = true }
        ],
        UpsertKeyField = "id",
        PostgreSqlDestination = new PostgreSqlDestinationOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "etl",
            Schema = "public",
            Table = "customers",
            ColumnMappings =
            [
                new PostgreSqlDestinationColumnMapping
                {
                    OutputField = "id",
                    DestinationColumn = "customer_id"
                },
                new PostgreSqlDestinationColumnMapping
                {
                    OutputField = "display_name",
                    DestinationColumn = "full_name"
                }
            ],
            UpsertKeyColumn = "customer_id"
        }
    };

    private static DataRow Row(string id, string name)
    {
        var row = new DataRow { SourceRowNumber = 2 };
        row.Values["id"] = id;
        row.Values["display_name"] = name;
        return row;
    }

    private static PostgreSqlBatchAttemptException Failure(
        bool isTransient,
        bool isDefinitelyUncommitted) => new(
            new IOException("simulated provider detail"),
            isTransient,
            isDefinitelyUncommitted);

    private sealed class SequencedExecutor : IPostgreSqlBatchExecutor
    {
        private readonly Queue<object> _outcomes;
        private readonly Func<PostgreSqlBatchCommand, CancellationToken, BatchLoadResult>? _callback;

        public SequencedExecutor(params object[] outcomes) => _outcomes = new Queue<object>(outcomes);

        public SequencedExecutor(Func<PostgreSqlBatchCommand, CancellationToken, BatchLoadResult> callback)
        {
            _callback = callback;
            _outcomes = [];
        }

        public int Attempts { get; private set; }

        public Task<BatchLoadResult> ExecuteAsync(
            IPostgreSqlConnectionFactory connectionFactory,
            string connectionProfile,
            string database,
            PostgreSqlBatchCommand command,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (_callback is not null)
            {
                return Task.FromResult(_callback(command, cancellationToken));
            }

            var outcome = _outcomes.Count > 1 ? _outcomes.Dequeue() : _outcomes.Peek();
            return outcome is Exception exception
                ? Task.FromException<BatchLoadResult>(exception)
                : Task.FromResult((BatchLoadResult)outcome);
        }
    }

    private sealed class UnusedConnectionFactory : IPostgreSqlConnectionFactory
    {
        public Task<DbConnection> OpenAsync(string connectionProfile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DbConnection> OpenDatabaseAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DestinationAccess : IPostgreSqlDestinationAccessService
    {
        public int Calls { get; private set; }

        public Task EnsureDestinationAccessibleAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class MetadataDiscovery(
        IReadOnlyList<PostgreSqlColumnMetadata> columns,
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> constraints) : IPostgreSqlMetadataDiscoveryService
    {
        public IReadOnlyList<PostgreSqlKeyConstraintMetadata> Constraints { get; set; } = constraints;

        public int ColumnCalls { get; private set; }

        public int ConstraintCalls { get; private set; }

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            ColumnCalls++;
            return Task.FromResult(columns);
        }

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            ConstraintCalls++;
            return Task.FromResult(Constraints);
        }

        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(
            string connectionProfile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(
            string connectionProfile,
            string database,
            string schema,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
