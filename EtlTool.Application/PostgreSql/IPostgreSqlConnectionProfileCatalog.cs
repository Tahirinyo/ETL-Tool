namespace EtlTool.Application.PostgreSql;

/// <summary>
/// Exposes the safe, logical names of configured PostgreSQL connection profiles.
/// </summary>
public interface IPostgreSqlConnectionProfileCatalog
{
    IReadOnlyList<string> GetProfileNames();
}
