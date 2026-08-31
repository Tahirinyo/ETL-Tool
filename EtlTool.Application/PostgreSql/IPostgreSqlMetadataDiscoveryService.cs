namespace EtlTool.Application.PostgreSql;

public interface IPostgreSqlMetadataDiscoveryService
{
    Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(
        string connectionProfile,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(
        string connectionProfile,
        string database,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(
        string connectionProfile,
        string database,
        string schema,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
        string connectionProfile,
        string database,
        string schema,
        string table,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
        string connectionProfile,
        string database,
        string schema,
        string table,
        CancellationToken cancellationToken);
}
