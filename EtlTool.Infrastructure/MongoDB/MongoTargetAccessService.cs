using System.Security.Cryptography;
using System.Text;
using EtlTool.Application.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoTargetAccessService : IMongoTargetAccessService
{
    private const int MaximumDatabaseNameBytes = 63;
    private const int NamespaceNotFoundErrorCode = 26;
    private const int IndexKeySpecsConflictErrorCode = 86;

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

    public async Task EnsureUpsertIndexAsync(
        MongoTarget target,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upsertKeyField);

        var validation = Validate(target);
        if (!validation.IsAllowed)
        {
            throw new InvalidOperationException(validation.FailureMessage);
        }

        try
        {
            var collection = _metadataDatabase
                .GetDatabase(target.DatabaseName)
                .GetCollection<BsonDocument>(target.CollectionName);

            if (await HasSuitableUpsertIndexAsync(
                    collection,
                    upsertKeyField,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await CreateCompatibleUpsertIndexAsync(
                    collection,
                    upsertKeyField,
                    indexName: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException exception)
                when (exception.Code == IndexKeySpecsConflictErrorCode)
            {
                if (await HasSuitableUpsertIndexAsync(
                        collection,
                        upsertKeyField,
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await CreateCompatibleUpsertIndexAsync(
                    collection,
                    upsertKeyField,
                    CreateFallbackIndexName(upsertKeyField),
                    cancellationToken).ConfigureAwait(false);

                if (!await HasSuitableUpsertIndexAsync(
                        collection,
                        upsertKeyField,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new MongoTargetAccessException();
                }
            }
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

    private static async Task<bool> HasSuitableUpsertIndexAsync(
        IMongoCollection<BsonDocument> collection,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        IAsyncCursor<BsonDocument> cursor;
        try
        {
            cursor = await collection.Indexes
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoCommandException exception)
            when (exception.Code == NamespaceNotFoundErrorCode)
        {
            return false;
        }

        using (cursor)
        {
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (cursor.Current.Any(index => IsSuitableUpsertIndex(index, upsertKeyField)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Task<string> CreateCompatibleUpsertIndexAsync(
        IMongoCollection<BsonDocument> collection,
        string upsertKeyField,
        string? indexName,
        CancellationToken cancellationToken) =>
        collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending(upsertKeyField),
                new CreateIndexOptions
                {
                    Name = indexName,
                    Collation = Collation.Simple
                }),
            cancellationToken: cancellationToken);

    private static string CreateFallbackIndexName(string upsertKeyField)
    {
        var fieldHash = SHA256.HashData(Encoding.UTF8.GetBytes(upsertKeyField));
        return $"etl_upsert_{Convert.ToHexString(fieldHash).ToLowerInvariant()}";
    }

    private static bool IsSuitableUpsertIndex(
        BsonDocument index,
        string upsertKeyField)
    {
        if (!index.TryGetValue("key", out var keyValue)
            || keyValue.BsonType != BsonType.Document)
        {
            return false;
        }

        var keys = keyValue.AsBsonDocument;
        if (keys.ElementCount == 0)
        {
            return false;
        }

        var leadingKey = keys.GetElement(0);
        if (!StringComparer.Ordinal.Equals(leadingKey.Name, upsertKeyField)
            || !IsAscendingOrDescending(leadingKey.Value)
            || IsEnabled(index, "sparse")
            || index.Contains("partialFilterExpression")
            || IsEnabled(index, "hidden"))
        {
            return false;
        }

        if (!index.TryGetValue("collation", out var collation))
        {
            return true;
        }

        return collation.BsonType == BsonType.Document
            && collation.AsBsonDocument.TryGetValue("locale", out var locale)
            && locale.IsString
            && StringComparer.Ordinal.Equals(locale.AsString, "simple");
    }

    private static bool IsAscendingOrDescending(BsonValue value) =>
        value switch
        {
            BsonInt32 direction => direction.Value is 1 or -1,
            BsonInt64 direction => direction.Value is 1 or -1,
            _ => false
        };

    private static bool IsEnabled(BsonDocument index, string optionName) =>
        index.TryGetValue(optionName, out var option)
        && option.BsonType == BsonType.Boolean
        && option.AsBoolean;
}
