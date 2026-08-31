using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
using Npgsql;
using EtlDataRow = EtlTool.Application.Extraction.DataRow;

namespace EtlTool.UnitTests.Infrastructure.PostgreSql;

public sealed class PostgreSqlEtlSourceTests
{
    [Fact]
    public async Task ReadAsync_IsDeferredCopiesIdentityAndDisposesResourcesAfterCompletion()
    {
        var reader = new TrackingDataReader(
            ["Id", "Name"],
            [[1, "Ada"], [2, DBNull.Value]]);
        var connection = new TrackingDbConnection(() => reader);
        var factory = new TrackingConnectionFactory(connection);
        var options = CreateOptions();
        await using var source = CreateSource(factory, options);

        var rows = source.ReadAsync(CancellationToken.None);
        options.ConnectionProfile = "Changed";
        options.Database = "changed";
        options.Schema = "changed";
        options.Table = "changed";

        Assert.Equal(0, factory.OpenCount);

        var result = await ReadAllAsync(rows);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal("ReportingDb", factory.ConnectionProfile);
        Assert.Equal("reporting", factory.Database);
        Assert.Equal(
            "SELECT * FROM \"Mixed \"\"Schema\".\"Rows \"\"Table\" ORDER BY \"Id\" ASC;",
            connection.Command.CommandText);
        Assert.Equal(CommandBehavior.SequentialAccess, connection.Command.Behavior);
        Assert.Equal([1L, 2L], result.Select(row => row.SourceRowNumber));
        Assert.Equal(1, result[0].Values["Id"]);
        Assert.Equal("Ada", result[0].Values["Name"]);
        Assert.Null(result[1].Values["Name"]);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.Command.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ReadAsync_YieldsFirstRowBeforeRequestingTheRemainderAndEarlyStopDisposesResources()
    {
        var reader = new TrackingDataReader(
            ["Id"],
            [[1], [2], [3]]);
        var connection = new TrackingDbConnection(() => reader);
        await using var source = CreateSource(
            new TrackingConnectionFactory(connection),
            CreateOptions());

        await using (var enumerator = source.ReadAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(1, enumerator.Current.Values["Id"]);
            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(0, reader.DisposeCount);
        }

        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.Command.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ReadAsync_ConsumerFailureDisposesResources()
    {
        var reader = new TrackingDataReader(["Id"], [[1], [2]]);
        var connection = new TrackingDbConnection(() => reader);
        await using var source = CreateSource(
            new TrackingConnectionFactory(connection),
            CreateOptions());

        await Assert.ThrowsAsync<ConsumerException>(() => ConsumeAndFailAsync(source));

        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.Command.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ReadAsync_ReadTimeoutUsesSafeAccessErrorAndDisposesResources()
    {
        var reader = new TrackingDataReader(["Id"], [[1]])
        {
            FailureReadNumber = 2
        };
        var connection = new TrackingDbConnection(() => reader);
        await using var source = CreateSource(
            new TrackingConnectionFactory(connection),
            CreateOptions());

        var exception = await Assert.ThrowsAsync<PostgreSqlConnectionAccessException>(
            () => ReadAllAsync(source.ReadAsync(CancellationToken.None)));

        Assert.Equal("The configured PostgreSQL source could not be accessed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.Command.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ReadAsync_ProviderReadFailureUsesSafeAccessErrorAndDisposesResources()
    {
        var reader = new TrackingDataReader(["Id"], [[1]])
        {
            FailureReadNumber = 1,
            ReadException = new NpgsqlException("secret provider detail")
        };
        var connection = new TrackingDbConnection(() => reader);
        await using var source = CreateSource(
            new TrackingConnectionFactory(connection),
            CreateOptions());

        var exception = await Assert.ThrowsAsync<PostgreSqlConnectionAccessException>(
            () => ReadAllAsync(source.ReadAsync(CancellationToken.None)));

        Assert.DoesNotContain("secret provider detail", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.Command.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ReadAsync_CommandTimeoutUsesSafeAccessErrorAndDisposesCreatedResources()
    {
        var connection = new TrackingDbConnection(
            () => throw new InvalidOperationException("A reader should not be created."));
        connection.Command.ExecutionException = new TimeoutException("secret detail");
        await using var source = CreateSource(
            new TrackingConnectionFactory(connection),
            CreateOptions());

        var exception = await Assert.ThrowsAsync<PostgreSqlConnectionAccessException>(
            () => ReadAllAsync(source.ReadAsync(CancellationToken.None)));

        Assert.DoesNotContain("secret detail", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, connection.Command.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ReadAsync_CancellationReachesActiveReadAndRemainsCancellation()
    {
        var reader = new TrackingDataReader(["Id"], []) { BlockReadUntilCanceled = true };
        var connection = new TrackingDbConnection(() => reader);
        var factory = new TrackingConnectionFactory(connection);
        await using var source = CreateSource(factory, CreateOptions());
        using var cancellation = new CancellationTokenSource();

        var moveNext = source
            .ReadAsync(cancellation.Token)
            .GetAsyncEnumerator()
            .MoveNextAsync()
            .AsTask();
        await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);

        Assert.Equal(cancellation.Token, factory.CancellationToken);
        Assert.Equal(cancellation.Token, connection.Command.CancellationToken);
        Assert.Equal(cancellation.Token, reader.ReadCancellationToken);
        Assert.Equal(1, reader.DisposeCount);
        Assert.Equal(1, connection.Command.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndRejectsNewEnumeration()
    {
        var source = CreateSource(
            new TrackingConnectionFactory(new TrackingDbConnection(
                () => new TrackingDataReader([], []))),
            CreateOptions());

        await source.DisposeAsync();
        await source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => ReadAllAsync(source.ReadAsync(CancellationToken.None)));
    }

    private static PostgreSqlSourceOptions CreateOptions() => new()
    {
        ConnectionProfile = "ReportingDb",
        Database = "reporting",
        Schema = "Mixed \"Schema",
        Table = "Rows \"Table"
    };

    [Fact]
    public async Task ReadAsync_UsesEveryCompositeKeyColumnWithSafeQuoting()
    {
        var reader = new TrackingDataReader(["Id"], [[1]]);
        var connection = new TrackingDbConnection(() => reader);
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> constraints =
        [
            new PostgreSqlKeyConstraintMetadata(
                "PK Rows",
                PostgreSqlKeyConstraintKind.PrimaryKey,
                [
                    new PostgreSqlKeyColumnMetadata("First \"Key", 1, false),
                    new PostgreSqlKeyColumnMetadata("Second Key", 2, false)
                ])
        ];
        await using var source = CreateSource(
            new TrackingConnectionFactory(connection),
            CreateOptions(),
            constraints);

        _ = await ReadAllAsync(source.ReadAsync(CancellationToken.None));

        Assert.Equal(
            "SELECT * FROM \"Mixed \"\"Schema\".\"Rows \"\"Table\" ORDER BY \"First \"\"Key\" ASC, \"Second Key\" ASC;",
            connection.Command.CommandText);
    }

    [Fact]
    public async Task ReadAsync_OrderingUnavailableDoesNotExecuteAnUnorderedDataCommand()
    {
        var connection = new TrackingDbConnection(() => new TrackingDataReader(["Id"], [[1]]));
        await using var source = CreateSource(
            new TrackingConnectionFactory(connection),
            CreateOptions(),
            []);

        await Assert.ThrowsAsync<PostgreSqlDeterministicOrderingUnavailableException>(
            () => ReadAllAsync(source.ReadAsync(CancellationToken.None)));

        Assert.Equal(0, connection.Command.ExecutionCount);
    }

    [Fact]
    public async Task ReadAsync_MetadataCancellationRemainsCancellationAndDoesNotOpenDataConnection()
    {
        var connection = new TrackingDbConnection(() => new TrackingDataReader(["Id"], [[1]]));
        var metadata = new CancelingMetadataDiscoveryService();
        await using var source = new PostgreSqlEtlSource(
            new TrackingConnectionFactory(connection),
            metadata,
            new PostgreSqlDeterministicOrderingResolver(),
            CreateOptions());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllAsync(source.ReadAsync(cancellation.Token)));

        Assert.Equal(cancellation.Token, metadata.CancellationToken);
        Assert.Equal(0, connection.Command.ExecutionCount);
    }

    private static PostgreSqlEtlSource CreateSource(
        IPostgreSqlConnectionFactory connectionFactory,
        PostgreSqlSourceOptions options,
        IReadOnlyList<PostgreSqlKeyConstraintMetadata>? constraints = null) => new(
            connectionFactory,
            new TrackingMetadataDiscoveryService(constraints ??
            [
                new PostgreSqlKeyConstraintMetadata(
                    "PK Rows",
                    PostgreSqlKeyConstraintKind.PrimaryKey,
                    [new PostgreSqlKeyColumnMetadata("Id", 1, false)])
            ]),
            new PostgreSqlDeterministicOrderingResolver(),
            options);

    private static async Task ConsumeAndFailAsync(PostgreSqlEtlSource source)
    {
        await foreach (var _ in source.ReadAsync(CancellationToken.None))
        {
            throw new ConsumerException();
        }
    }

    private static async Task<IReadOnlyList<EtlDataRow>> ReadAllAsync(
        IAsyncEnumerable<EtlDataRow> rows)
    {
        var result = new List<EtlDataRow>();
        await foreach (var row in rows)
        {
            result.Add(row);
        }

        return result;
    }

    private sealed class ConsumerException : Exception;

    private sealed class TrackingConnectionFactory(DbConnection connection) : IPostgreSqlConnectionFactory
    {
        public int OpenCount { get; private set; }

        public string? ConnectionProfile { get; private set; }

        public string? Database { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<DbConnection> OpenAsync(
            string connectionProfile,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DbConnection> OpenDatabaseAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            ConnectionProfile = connectionProfile;
            Database = database;
            CancellationToken = cancellationToken;
            return Task.FromResult(connection);
        }
    }

    private sealed class TrackingMetadataDiscoveryService(
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> constraints) : IPostgreSqlMetadataDiscoveryService
    {
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

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) => Task.FromResult(constraints);
    }

    private sealed class CancelingMetadataDiscoveryService : IPostgreSqlMetadataDiscoveryService
    {
        public CancellationToken CancellationToken { get; private set; }

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

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            return Task.FromCanceled<IReadOnlyList<PostgreSqlKeyConstraintMetadata>>(cancellationToken);
        }
    }

    private sealed class TrackingDbConnection(Func<DbDataReader> createReader) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        public TrackingDbCommand Command { get; } = new(createReader);

        public int DisposeCount { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "reporting";

        public override string DataSource => "test";

        public override string ServerVersion => "test";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
        {
            Command.Connection = this;
            return Command;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                _state = ConnectionState.Closed;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingDbCommand(Func<DbDataReader> createReader) : DbCommand
    {
        public Exception? ExecutionException { get; set; }

        public CommandBehavior Behavior { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public int DisposeCount { get; private set; }

        public int ExecutionCount { get; private set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } =
            new EmptyParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            Behavior = behavior;
            CancellationToken = cancellationToken;
            return ExecutionException is null
                ? Task.FromResult(createReader())
                : Task.FromException<DbDataReader>(ExecutionException);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingDataReader(
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows) : DbDataReader
    {
        private int _index = -1;
        private bool _isClosed;

        public int? FailureReadNumber { get; init; }

        public Exception? ReadException { get; init; }

        public bool BlockReadUntilCanceled { get; init; }

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ReadCancellationToken { get; private set; }

        public int ReadCount { get; private set; }

        public int DisposeCount { get; private set; }

        public override int Depth => 0;

        public override int FieldCount => columns.Count;

        public override bool HasRows => rows.Count > 0;

        public override bool IsClosed => _isClosed;

        public override int RecordsAffected => -1;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        public override char GetChar(int ordinal) => (char)GetValue(ordinal);

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

        public override Type GetFieldType(int ordinal) => GetValue(ordinal).GetType();

        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

        public override string GetName(int ordinal) => columns[ordinal];

        public override int GetOrdinal(string name)
        {
            for (var ordinal = 0; ordinal < columns.Count; ordinal++)
            {
                if (string.Equals(columns[ordinal], name, StringComparison.Ordinal))
                {
                    return ordinal;
                }
            }

            throw new IndexOutOfRangeException(name);
        }

        public override string GetString(int ordinal) => (string)GetValue(ordinal);

        public override object GetValue(int ordinal) => rows[_index][ordinal] ?? DBNull.Value;

        public override int GetValues(object[] values)
        {
            var count = Math.Min(values.Length, FieldCount);
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                values[ordinal] = GetValue(ordinal);
            }

            return count;
        }

        public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

        public override bool NextResult() => false;

        public override bool Read() => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => rows.GetEnumerator();

        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            ReadCancellationToken = cancellationToken;
            ReadStarted.TrySetResult();

            if (BlockReadUntilCanceled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailureReadNumber == ReadCount)
            {
                throw ReadException ?? new TimeoutException("provider read failed");
            }

            return ++_index < rows.Count;
        }

        public override Task<bool> IsDBNullAsync(
            int ordinal,
            CancellationToken cancellationToken) =>
            Task.FromResult(IsDBNull(ordinal));

        public override Task<T> GetFieldValueAsync<T>(
            int ordinal,
            CancellationToken cancellationToken) =>
            Task.FromResult((T)GetValue(ordinal));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                _isClosed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class EmptyParameterCollection : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot { get; } = new();

        public override int Add(object value) => throw new NotSupportedException();

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear()
        {
        }

        public override bool Contains(object value) => false;

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index)
        {
        }

        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

        public override int IndexOf(object value) => -1;

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => throw new NotSupportedException();

        public override void Remove(object value)
        {
        }

        public override void RemoveAt(int index)
        {
        }

        public override void RemoveAt(string parameterName)
        {
        }

        protected override DbParameter GetParameter(int index) => throw new IndexOutOfRangeException();

        protected override DbParameter GetParameter(string parameterName) => throw new IndexOutOfRangeException();

        protected override void SetParameter(int index, DbParameter value) => throw new IndexOutOfRangeException();

        protected override void SetParameter(string parameterName, DbParameter value) => throw new IndexOutOfRangeException();
    }
}
