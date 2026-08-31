using System.Data.Common;
using EtlTool.Application.PostgreSql;
using Npgsql;

namespace EtlTool.Infrastructure.PostgreSql;

public sealed class PostgreSqlMetadataDiscoveryService(
    IPostgreSqlConnectionFactory connectionFactory) : IPostgreSqlMetadataDiscoveryService
{
    private const string DatabasesQuery = """
        SELECT datname
        FROM pg_catalog.pg_database
        WHERE datallowconn
          AND has_database_privilege(datname, 'CONNECT')
        ORDER BY datname;
        """;

    private const string SchemasQuery = """
        SELECT schema_name
        FROM information_schema.schemata
        WHERE has_schema_privilege(schema_name, 'USAGE')
        ORDER BY schema_name;
        """;

    private const string SchemaExistsQuery = """
        SELECT EXISTS (
            SELECT 1
            FROM information_schema.schemata
            WHERE schema_name = @schema
              AND has_schema_privilege(schema_name, 'USAGE'));
        """;

    private const string TablesQuery = """
        SELECT c.relname
        FROM pg_catalog.pg_class AS c
        INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema
          AND c.relkind IN ('r', 'p')
          AND has_table_privilege(c.oid, 'SELECT')
        ORDER BY c.relname;
        """;

    private const string TableExistsQuery = """
        SELECT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_class AS c
            INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relname = @table
              AND c.relkind IN ('r', 'p')
              AND has_table_privilege(c.oid, 'SELECT'));
        """;

    private const string ColumnsQuery = """
        SELECT a.attname,
               pg_catalog.format_type(a.atttypid, a.atttypmod),
               NOT a.attnotnull,
               a.attnum
        FROM pg_catalog.pg_attribute AS a
        INNER JOIN pg_catalog.pg_class AS c ON c.oid = a.attrelid
        INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema
          AND c.relname = @table
          AND c.relkind IN ('r', 'p')
          AND has_table_privilege(c.oid, 'SELECT')
          AND a.attnum > 0
          AND NOT a.attisdropped
        ORDER BY a.attnum;
        """;

    public async Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(
        string connectionProfile,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory
            .OpenAsync(connectionProfile, cancellationToken)
            .ConfigureAwait(false);

        return await ExecuteReaderAsync(
            connection,
            DatabasesQuery,
            configure: null,
            reader => new PostgreSqlDatabaseMetadata(reader.GetString(0)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(
        string connectionProfile,
        string database,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory
            .OpenDatabaseAsync(connectionProfile, database, cancellationToken)
            .ConfigureAwait(false);

        return await ExecuteReaderAsync(
            connection,
            SchemasQuery,
            configure: null,
            reader => new PostgreSqlSchemaMetadata(reader.GetString(0)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(
        string connectionProfile,
        string database,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory
            .OpenDatabaseAsync(connectionProfile, database, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSchemaExistsAsync(connection, schema, cancellationToken).ConfigureAwait(false);

        return await ExecuteReaderAsync(
            connection,
            TablesQuery,
            command => AddParameter(command, "@schema", schema),
            reader => new PostgreSqlTableMetadata(reader.GetString(0)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
        string connectionProfile,
        string database,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory
            .OpenDatabaseAsync(connectionProfile, database, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSchemaExistsAsync(connection, schema, cancellationToken).ConfigureAwait(false);
        await EnsureTableExistsAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);

        return await ExecuteReaderAsync(
            connection,
            ColumnsQuery,
            command =>
            {
                AddParameter(command, "@schema", schema);
                AddParameter(command, "@table", table);
            },
            reader => new PostgreSqlColumnMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetInt16(3)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaExistsAsync(
        DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        if (!await ExecuteExistsAsync(
                connection,
                SchemaExistsQuery,
                command => AddParameter(command, "@schema", schema),
                cancellationToken).ConfigureAwait(false))
        {
            throw new PostgreSqlMetadataObjectNotFoundException("schema");
        }
    }

    private static async Task EnsureTableExistsAsync(
        DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);
        if (!await ExecuteExistsAsync(
                connection,
                TableExistsQuery,
                command =>
                {
                    AddParameter(command, "@schema", schema);
                    AddParameter(command, "@table", table);
                },
                cancellationToken).ConfigureAwait(false))
        {
            throw new PostgreSqlMetadataObjectNotFoundException("table");
        }
    }

    private static async Task<bool> ExecuteExistsAsync(
        DbConnection connection,
        string query,
        Action<DbCommand> configure,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            configure(command);
            return Convert.ToBoolean(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
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

    private static async Task<IReadOnlyList<T>> ExecuteReaderAsync<T>(
        DbConnection connection,
        string query,
        Action<DbCommand>? configure,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            configure?.Invoke(command);

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var results = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(map(reader));
            }

            return results;
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

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
