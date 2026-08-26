using System.Text;
using EtlTool.Application.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoTargetAccessService : IMongoTargetAccessService
{
    private const int MaximumDatabaseNameBytes = 63;

    private static readonly HashSet<string> SystemDatabases = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "config",
        "local"
    };

    private static readonly char[] InvalidDatabaseNameCharacters =
        ['/', '\\', '.', ' ', '"', '$', '\0'];

    private readonly MongoMetadataDatabase _metadataDatabase;
    private readonly string _metadataDatabaseName;

    public MongoTargetAccessService(
        MongoMetadataDatabase metadataDatabase,
        MongoDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _metadataDatabase = metadataDatabase;
        _metadataDatabaseName = options.MetadataDatabaseName;
    }

    public MongoTargetValidationResult Validate(MongoTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(target.DatabaseName))
        {
            return MongoTargetValidationResult.Rejected(
                "The destination database must be configured.");
        }

        if (string.IsNullOrWhiteSpace(target.CollectionName))
        {
            return MongoTargetValidationResult.Rejected(
                "The destination collection must be configured.");
        }

        if (SystemDatabases.Contains(target.DatabaseName)
            || string.Equals(
                target.DatabaseName,
                _metadataDatabaseName,
                StringComparison.OrdinalIgnoreCase))
        {
            return MongoTargetValidationResult.Rejected(
                "The configured destination database cannot be used as an ETL target.");
        }

        if (target.DatabaseName.IndexOfAny(InvalidDatabaseNameCharacters) >= 0
            || Encoding.UTF8.GetByteCount(target.DatabaseName) > MaximumDatabaseNameBytes
            || target.CollectionName.Contains('$', StringComparison.Ordinal)
            || target.CollectionName.StartsWith("system.", StringComparison.Ordinal)
            || target.CollectionName.Contains(".system.", StringComparison.Ordinal))
        {
            return MongoTargetValidationResult.Rejected(
                "The configured MongoDB destination name is not valid.");
        }

        try
        {
            var databaseNamespace = new DatabaseNamespace(target.DatabaseName);
            _ = new CollectionNamespace(databaseNamespace, target.CollectionName);
        }
        catch (ArgumentException)
        {
            return MongoTargetValidationResult.Rejected(
                "The configured MongoDB destination name is not valid.");
        }

        return MongoTargetValidationResult.Allowed;
    }

    public async Task EnsureAccessibleAsync(
        MongoTarget target,
        CancellationToken cancellationToken)
    {
        var validation = Validate(target);
        if (!validation.IsAllowed)
        {
            throw new InvalidOperationException(validation.FailureMessage);
        }

        try
        {
            var database = _metadataDatabase.GetDatabase(target.DatabaseName);
            if (await ContainsTargetCollectionAsync(
                    database,
                    target.CollectionName,
                    authorizedCollections: true,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // An empty authorized result is either a hidden existing collection or a
            // nonexistent collection. Only a database-capable credential can confirm
            // that the latter remains a permitted future loader destination.
            await ContainsTargetCollectionAsync(
                database,
                target.CollectionName,
                authorizedCollections: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MongoException)
        {
            throw new MongoTargetAccessException();
        }
        catch (TimeoutException)
        {
            throw new MongoTargetAccessException();
        }
    }

    private static async Task<bool> ContainsTargetCollectionAsync(
        IMongoDatabase database,
        string collectionName,
        bool authorizedCollections,
        CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions
            {
                AuthorizedCollections = authorizedCollections,
                Filter = new BsonDocument("name", collectionName)
            },
            cancellationToken).ConfigureAwait(false);

        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (cursor.Current.Contains(collectionName, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
