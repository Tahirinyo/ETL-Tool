namespace EtlTool.Application.MongoDB;

public interface IMongoSourceMetadataDiscoveryService
{
    Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverDatabasesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverCollectionsAsync(
        string database,
        CancellationToken cancellationToken);
}
