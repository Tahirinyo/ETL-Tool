using System.Data.Common;
using EtlTool.Application.PostgreSql;
using Npgsql;

namespace EtlTool.Infrastructure.PostgreSql;

public sealed class PostgreSqlConnectionFactory :
    IPostgreSqlConnectionFactory,
    IPostgreSqlConnectionProfileCatalog
{
    private readonly IReadOnlyDictionary<string, string> _connectionStrings;
    private readonly IReadOnlyList<string> _profileNames;

    public PostgreSqlConnectionFactory(PostgreSqlConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _connectionStrings = options.Profiles.ToDictionary(
            profile => profile.Key,
            profile => profile.Value.ConnectionString,
            StringComparer.OrdinalIgnoreCase);
        _profileNames = _connectionStrings.Keys
            .OrderBy(profileName => profileName, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> GetProfileNames() => _profileNames;

    public async Task<DbConnection> OpenAsync(
        string connectionProfile,
        CancellationToken cancellationToken)
    {
        var connectionString = ResolveConnectionString(connectionProfile);
        return await OpenConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DbConnection> OpenDatabaseAsync(
        string connectionProfile,
        string database,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        var connectionString = ResolveConnectionString(connectionProfile);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = database
        };

        return await OpenConnectionAsync(builder.ConnectionString, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveConnectionString(string connectionProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionProfile);

        if (!_connectionStrings.TryGetValue(connectionProfile, out var connectionString))
        {
            throw new PostgreSqlConnectionProfileNotFoundException();
        }

        return connectionString;
    }

    private static async Task<DbConnection> OpenConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (NpgsqlException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new PostgreSqlConnectionAccessException();
        }
        catch (TimeoutException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new PostgreSqlConnectionAccessException();
        }
    }
}
