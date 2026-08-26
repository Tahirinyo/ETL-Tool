using EtlTool.Application.Loading;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

internal interface IMongoBulkWriteExecutor
{
    Task<BatchLoadResult> WriteAsync(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<WriteModel<BsonDocument>> requests,
        CancellationToken cancellationToken);
}

internal sealed class MongoBulkWriteExecutor : IMongoBulkWriteExecutor
{
    public async Task<BatchLoadResult> WriteAsync(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<WriteModel<BsonDocument>> requests,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await collection.BulkWriteAsync(
                requests,
                new BulkWriteOptions { IsOrdered = true },
                cancellationToken).ConfigureAwait(false);

            return ToLoadResult(result);
        }
        catch (MongoBulkWriteException<BsonDocument> exception)
        {
            throw new BatchLoadException(
                "MongoDB rejected part of the ordered batch.",
                ToLoadResult(exception.Result),
                exception);
        }
    }

    private static BatchLoadResult ToLoadResult(BulkWriteResult<BsonDocument> result) =>
        new(result.Upserts.Count, result.MatchedCount);
}
