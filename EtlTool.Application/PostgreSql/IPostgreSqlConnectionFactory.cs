using System.Data.Common;

namespace EtlTool.Application.PostgreSql;

public interface IPostgreSqlConnectionFactory
{
    Task<DbConnection> OpenAsync(
        string connectionProfile,
        CancellationToken cancellationToken);

    Task<DbConnection> OpenDatabaseAsync(
        string connectionProfile,
        string database,
        CancellationToken cancellationToken);
}
