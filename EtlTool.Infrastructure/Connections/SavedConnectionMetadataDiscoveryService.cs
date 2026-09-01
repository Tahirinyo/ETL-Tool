using EtlTool.Application.Connections;
using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;

namespace EtlTool.Infrastructure.Connections;

public sealed class SavedConnectionMetadataDiscoveryService : ISavedConnectionMetadataDiscoveryService
{
    private readonly ISavedConnectionRevisionResolver _resolver;
    private readonly SavedConnectionProviderFactory _providerFactory;

    public SavedConnectionMetadataDiscoveryService(
        ISavedConnectionRevisionResolver resolver,
        SavedConnectionProviderFactory providerFactory)
    {
        _resolver = resolver;
        _providerFactory = providerFactory;
    }

    public async Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverPostgreSqlDatabasesAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await PostgreSqlAsync(connectionId, cancellationToken, context => context.MetadataDiscovery.DiscoverDatabasesAsync(SavedConnectionProviderFactory.RuntimePostgreSqlProfile, cancellationToken));

    public async Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverPostgreSqlSchemasAsync(Guid connectionId, string database, CancellationToken cancellationToken) =>
        await PostgreSqlAsync(connectionId, cancellationToken, context => context.MetadataDiscovery.DiscoverSchemasAsync(SavedConnectionProviderFactory.RuntimePostgreSqlProfile, database, cancellationToken));

    public async Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverPostgreSqlTablesAsync(Guid connectionId, string database, string schema, CancellationToken cancellationToken) =>
        await PostgreSqlAsync(connectionId, cancellationToken, context => context.MetadataDiscovery.DiscoverTablesAsync(SavedConnectionProviderFactory.RuntimePostgreSqlProfile, database, schema, cancellationToken));

    public async Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverPostgreSqlColumnsAsync(Guid connectionId, string database, string schema, string table, CancellationToken cancellationToken) =>
        await PostgreSqlAsync(connectionId, cancellationToken, context => context.MetadataDiscovery.DiscoverColumnsAsync(SavedConnectionProviderFactory.RuntimePostgreSqlProfile, database, schema, table, cancellationToken));

    public async Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverPostgreSqlKeyConstraintsAsync(Guid connectionId, string database, string schema, string table, CancellationToken cancellationToken) =>
        await PostgreSqlAsync(connectionId, cancellationToken, context => context.MetadataDiscovery.DiscoverKeyConstraintsAsync(SavedConnectionProviderFactory.RuntimePostgreSqlProfile, database, schema, table, cancellationToken));

    public async Task EnsurePostgreSqlDestinationAccessibleAsync(Guid connectionId, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var context = await PostgreSqlContextAsync(connectionId, cancellationToken);
        await context.MetadataDiscovery.EnsureDestinationAccessibleAsync(SavedConnectionProviderFactory.RuntimePostgreSqlProfile, database, schema, table, cancellationToken);
    }

    public async Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverMongoDatabasesAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var discovery = await MongoDiscoveryAsync(connectionId, cancellationToken);
        return await discovery.DiscoverDatabasesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverMongoCollectionsAsync(Guid connectionId, string database, CancellationToken cancellationToken)
    {
        var discovery = await MongoDiscoveryAsync(connectionId, cancellationToken);
        return await discovery.DiscoverCollectionsAsync(database, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceFieldDefinition>> InferMongoSchemaAsync(Guid connectionId, string database, string collection, CancellationToken cancellationToken)
    {
        var reference = await ResolveAsync(connectionId, DatabaseProviderType.MongoDb, cancellationToken);
        var context = await _providerFactory.CreateMongoDbAsync(reference.ConnectionId, reference.Revision, cancellationToken);
        return await context.SchemaInference.InferAsync(new MongoDbSourceOptions { SavedConnectionId = connectionId, Database = database, Collection = collection }, cancellationToken);
    }

    private async Task<T> PostgreSqlAsync<T>(Guid connectionId, CancellationToken cancellationToken, Func<PostgreSqlRuntimeConnectionContext, Task<T>> operation) =>
        await operation(await PostgreSqlContextAsync(connectionId, cancellationToken));

    private async Task<PostgreSqlRuntimeConnectionContext> PostgreSqlContextAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var reference = await ResolveAsync(connectionId, DatabaseProviderType.PostgreSql, cancellationToken);
        return await _providerFactory.CreatePostgreSqlAsync(reference.ConnectionId, reference.Revision, cancellationToken);
    }

    private async Task<MongoSourceMetadataDiscoveryService> MongoDiscoveryAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var reference = await ResolveAsync(connectionId, DatabaseProviderType.MongoDb, cancellationToken);
        var context = await _providerFactory.CreateMongoDbAsync(reference.ConnectionId, reference.Revision, cancellationToken);
        return new MongoSourceMetadataDiscoveryService(context.MetadataDatabase, context.TargetAccess);
    }

    private Task<SavedConnectionReference> ResolveAsync(Guid connectionId, DatabaseProviderType providerType, CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty) throw new SavedConnectionResolutionException();
        return _resolver.ResolveCurrentAsync(connectionId, providerType, cancellationToken);
    }
}
