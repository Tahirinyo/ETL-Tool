using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.ValueObjects;
using Npgsql;
using EtlDataRow = EtlTool.Application.Extraction.DataRow;

namespace EtlTool.Infrastructure.PostgreSql;

public sealed class PostgreSqlEtlSource : IEtlSource
{
    private readonly IPostgreSqlConnectionFactory _connectionFactory;
    private readonly IPostgreSqlMetadataDiscoveryService _metadataDiscoveryService;
    private readonly PostgreSqlDeterministicOrderingResolver _orderingResolver;
    private readonly PostgreSqlSourceOptions _options;
    private int _disposed;

    public PostgreSqlEtlSource(
        IPostgreSqlConnectionFactory connectionFactory,
        IPostgreSqlMetadataDiscoveryService metadataDiscoveryService,
        PostgreSqlDeterministicOrderingResolver orderingResolver,
        PostgreSqlSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(metadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(orderingResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Table);

        _connectionFactory = connectionFactory;
        _metadataDiscoveryService = metadataDiscoveryService;
        _orderingResolver = orderingResolver;
        _options = new PostgreSqlSourceOptions
        {
            ConnectionProfile = options.ConnectionProfile,
            Database = options.Database,
            Schema = options.Schema,
            Table = options.Table
        };
    }

    public async IAsyncEnumerable<EtlDataRow> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var ordering = _orderingResolver.Resolve(
            await _metadataDiscoveryService.DiscoverKeyConstraintsAsync(
                _options.ConnectionProfile,
                _options.Database,
                _options.Schema,
                _options.Table,
                cancellationToken).ConfigureAwait(false));
        await using var connection = await _connectionFactory
            .OpenDatabaseAsync(
                _options.ConnectionProfile,
                _options.Database,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildQuery(ordering);

        await using var reader = await ExecuteReaderAsync(command, cancellationToken)
            .ConfigureAwait(false);
        long sourceRowNumber = 0;

        while (await ReadNextAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            var row = new EtlDataRow { SourceRowNumber = ++sourceRowNumber };
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                row.Values.Add(
                    reader.GetName(ordinal),
                    await ReadValueAsync(reader, ordinal, cancellationToken).ConfigureAwait(false));
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private static async Task<DbDataReader> ExecuteReaderAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            throw new PostgreSqlConnectionAccessException();
        }
        catch (TimeoutException)
        {
            throw new PostgreSqlConnectionAccessException();
        }
    }

    private static async Task<bool> ReadNextAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            throw new PostgreSqlConnectionAccessException();
        }
        catch (TimeoutException)
        {
            throw new PostgreSqlConnectionAccessException();
        }
    }

    private static async Task<object?> ReadValueAsync(
        DbDataReader reader,
        int ordinal,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return await reader
                .GetFieldValueAsync<object>(ordinal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            throw new PostgreSqlConnectionAccessException();
        }
        catch (TimeoutException)
        {
            throw new PostgreSqlConnectionAccessException();
        }
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private string BuildQuery(PostgreSqlSourceOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(ordering);

        var orderBy = string.Join(
            ", ",
            ordering.Columns.Select(column => $"{QuoteIdentifier(column)} ASC"));
        return $"SELECT * FROM {QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(_options.Table)} ORDER BY {orderBy};";
    }
}
