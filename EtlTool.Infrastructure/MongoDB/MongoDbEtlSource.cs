using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
using EtlTool.Application.MongoDB;
using EtlTool.Domain.ValueObjects;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoDbEtlSource : IEtlSource
{
    private readonly MongoMetadataDatabase _metadataDatabase;
    private readonly MongoDbSourceOptions _sourceOptions;
    private readonly MongoSourceDocumentConverter _converter;
    private readonly int _fetchSize;
    private int _disposed;

    public MongoDbEtlSource(
        MongoMetadataDatabase metadataDatabase,
        MongoDbOptions options,
        MongoDbSourceOptions sourceOptions,
        IReadOnlyList<SourceFieldDefinition> expectedSchema)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sourceOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOptions.Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOptions.Collection);

        options.Validate();
        _metadataDatabase = metadataDatabase;
        _sourceOptions = new MongoDbSourceOptions
        {
            Database = sourceOptions.Database,
            Collection = sourceOptions.Collection
        };
        _converter = new MongoSourceDocumentConverter(expectedSchema);
        _fetchSize = options.SourceExecutionFetchSize;
    }

    public async IAsyncEnumerable<DataRow> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var collection = _metadataDatabase
            .GetDatabase(_sourceOptions.Database)
            .GetCollection<BsonDocument>(_sourceOptions.Collection);
        var options = new FindOptions<BsonDocument>
        {
            Sort = Builders<BsonDocument>.Sort.Ascending("_id"),
            BatchSize = _fetchSize
        };

        using var cursor = await CreateCursorAsync(
                collection,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        long sourceRowNumber = 0;

        while (await MoveNextAsync(cursor, cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return _converter.Convert(
                    document,
                    ++sourceRowNumber,
                    cancellationToken);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private static async Task<IAsyncCursor<BsonDocument>> CreateCursorAsync(
        IMongoCollection<BsonDocument> collection,
        FindOptions<BsonDocument> options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await collection
                .FindAsync(
                    Builders<BsonDocument>.Filter.Empty,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
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

    private static async Task<bool> MoveNextAsync(
        IAsyncCursor<BsonDocument> cursor,
        CancellationToken cancellationToken)
    {
        try
        {
            return await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false);
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
