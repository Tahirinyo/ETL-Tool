using EtlTool.Application.MongoDB;
using EtlTool.Domain.ValueObjects;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoSourceSchemaInferenceService : IMongoSourceSchemaInferenceService
{
    private readonly MongoMetadataDatabase _metadataDatabase;
    private readonly IMongoSourceMetadataDiscoveryService _metadataDiscoveryService;
    private readonly int _sampleDocumentLimit;

    public MongoSourceSchemaInferenceService(
        MongoMetadataDatabase metadataDatabase,
        IMongoSourceMetadataDiscoveryService metadataDiscoveryService,
        MongoDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        ArgumentNullException.ThrowIfNull(metadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _metadataDatabase = metadataDatabase;
        _metadataDiscoveryService = metadataDiscoveryService;
        _sampleDocumentLimit = options.SourceSchemaSampleDocumentLimit;
    }

    public async Task<IReadOnlyList<SourceFieldDefinition>> InferAsync(
        MongoDbSourceOptions source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Collection);
        cancellationToken.ThrowIfCancellationRequested();

        var collections = await _metadataDiscoveryService
            .DiscoverCollectionsAsync(source.Database, cancellationToken)
            .ConfigureAwait(false);
        if (!collections.Any(collection => string.Equals(
                collection.Name,
                source.Collection,
                StringComparison.Ordinal)))
        {
            throw new MongoSourceMetadataObjectNotFoundException("collection");
        }

        try
        {
            var collection = _metadataDatabase
                .GetDatabase(source.Database)
                .GetCollection<BsonDocument>(source.Collection);
            var options = new FindOptions<BsonDocument>
            {
                Sort = Builders<BsonDocument>.Sort.Ascending("_id"),
                Limit = _sampleDocumentLimit,
                BatchSize = _sampleDocumentLimit
            };

            using var cursor = await collection
                .FindAsync(
                    Builders<BsonDocument>.Filter.Empty,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            var accumulator = new MongoSourceSchemaAccumulator();
            var inspectedDocumentCount = 0;

            while (inspectedDocumentCount < _sampleDocumentLimit
                   && await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var document in cursor.Current)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inspectedDocumentCount >= _sampleDocumentLimit)
                    {
                        break;
                    }

                    accumulator.Observe(document, cancellationToken);
                    inspectedDocumentCount++;
                }
            }

            return accumulator.Build();
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
}
