using EtlTool.Application.MongoDB;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoSourceMetadataDiscoveryService : IMongoSourceMetadataDiscoveryService
{
    // This value is only used to reuse the target namespace safety policy when
    // validating a database; it is never persisted or sent to MongoDB.
    private const string PolicyProbeCollectionName = "etl_source_discovery";

    private readonly MongoMetadataDatabase _metadataDatabase;
    private readonly IMongoTargetAccessService _targetAccessService;

    public MongoSourceMetadataDiscoveryService(
        MongoMetadataDatabase metadataDatabase,
        IMongoTargetAccessService targetAccessService)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        ArgumentNullException.ThrowIfNull(targetAccessService);

        _metadataDatabase = metadataDatabase;
        _targetAccessService = targetAccessService;
    }

    public async Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverDatabasesAsync(
        CancellationToken cancellationToken)
    {
        var names = await DiscoverAllowedDatabaseNamesAsync(cancellationToken).ConfigureAwait(false);
        return names.Select(name => new MongoDatabaseMetadata(name)).ToArray();
    }

    public async Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverCollectionsAsync(
        string database,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        EnsureDatabaseIsAllowed(database);

        var databases = await DiscoverAllowedDatabaseNamesAsync(cancellationToken).ConfigureAwait(false);
        if (!databases.Contains(database, StringComparer.Ordinal))
        {
            throw new MongoSourceMetadataObjectNotFoundException("database");
        }

        try
        {
            var mongoDatabase = _metadataDatabase.GetDatabase(database);
            using var cursor = await mongoDatabase.ListCollectionNamesAsync(
                new ListCollectionNamesOptions { AuthorizedCollections = true },
                cancellationToken).ConfigureAwait(false);

            var collections = new List<MongoCollectionMetadata>();
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var name in cursor.Current)
                {
                    if (_targetAccessService.Validate(new MongoTarget(database, name)).IsAllowed)
                    {
                        collections.Add(new MongoCollectionMetadata(name));
                    }
                }
            }

            return collections
                .OrderBy(collection => collection.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MongoException)
        {
            throw new MongoSourceAccessException();
        }
        catch (TimeoutException)
        {
            throw new MongoSourceAccessException();
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverAllowedDatabaseNamesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var cursor = await _metadataDatabase
                .ListDatabaseNamesAsync(cancellationToken)
                .ConfigureAwait(false);
            var names = new List<string>();
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var name in cursor.Current)
                {
                    if (_targetAccessService.Validate(
                            new MongoTarget(name, PolicyProbeCollectionName)).IsAllowed)
                    {
                        names.Add(name);
                    }
                }
            }

            return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MongoException)
        {
            throw new MongoSourceAccessException();
        }
        catch (TimeoutException)
        {
            throw new MongoSourceAccessException();
        }
    }

    private void EnsureDatabaseIsAllowed(string database)
    {
        if (_targetAccessService.Validate(
                new MongoTarget(database, PolicyProbeCollectionName)).IsAllowed)
        {
            return;
        }

        throw new InvalidOperationException(
            "The selected MongoDB database cannot be used as an ETL source.");
    }
}
