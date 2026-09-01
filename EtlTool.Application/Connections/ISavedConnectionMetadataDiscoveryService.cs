using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Connections;

/// <summary>
/// Discovers safe logical metadata through a saved connection. Implementations must
/// resolve protected connection material server-side.
/// </summary>
public interface ISavedConnectionMetadataDiscoveryService
{
    Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverPostgreSqlDatabasesAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverPostgreSqlSchemasAsync(Guid connectionId, string database, CancellationToken cancellationToken);
    Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverPostgreSqlTablesAsync(Guid connectionId, string database, string schema, CancellationToken cancellationToken);
    Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverPostgreSqlColumnsAsync(Guid connectionId, string database, string schema, string table, CancellationToken cancellationToken);
    Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverPostgreSqlKeyConstraintsAsync(Guid connectionId, string database, string schema, string table, CancellationToken cancellationToken);
    Task EnsurePostgreSqlDestinationAccessibleAsync(Guid connectionId, string database, string schema, string table, CancellationToken cancellationToken);
    Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverMongoDatabasesAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverMongoCollectionsAsync(Guid connectionId, string database, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceFieldDefinition>> InferMongoSchemaAsync(Guid connectionId, string database, string collection, CancellationToken cancellationToken);
}
