namespace EtlTool.Application.PostgreSql;

/// <summary>
/// Confirms that a selected PostgreSQL table is usable as a write destination.
/// </summary>
public interface IPostgreSqlDestinationAccessService
{
    Task EnsureDestinationAccessibleAsync(
        string connectionProfile,
        string database,
        string schema,
        string table,
        CancellationToken cancellationToken);
}
