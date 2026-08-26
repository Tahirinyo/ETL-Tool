using EtlTool.Application.Extraction;
using EtlTool.Application.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoBulkUpsertLoaderTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task BulkWrite_FirstRunAndRerunAreIdempotentAndCountMatchesAsUpdates()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var collectionName = $"rows_{Guid.NewGuid():N}";
        var target = new MongoTarget(testDatabase.DatabaseName, collectionName);
        var firstRows = new[]
        {
            Row(2, ("externalId", "A"), ("name", "Ada")),
            Row(3, ("externalId", "B"), ("name", "Grace"))
        };

        var first = await testDatabase.Loader.UpsertBatchAsync(
            firstRows,
            target,
            "externalId",
            CancellationToken.None);
        var rerun = await testDatabase.Loader.UpsertBatchAsync(
            [
                Row(2, ("externalId", "A"), ("name", "Ada")),
                Row(3, ("externalId", "B"), ("name", "Grace"))
            ],
            target,
            "externalId",
            CancellationToken.None);
        var collection = testDatabase.Database.GetCollection<BsonDocument>(collectionName);

        Assert.Equal((2L, 0L), (first.InsertedRows, first.UpdatedRows));
        Assert.Equal((0L, 2L), (rerun.InsertedRows, rerun.UpdatedRows));
        Assert.Equal(2, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task BulkWrite_UsesConfiguredIdentityAndReportsMixedInsertUpdateCounts()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var collectionName = $"rows_{Guid.NewGuid():N}";
        var target = new MongoTarget(testDatabase.DatabaseName, collectionName);
        await testDatabase.Loader.UpsertBatchAsync(
            [Row(2, ("externalId", "A"), ("sourceId", "old"), ("name", "Ada"))],
            target,
            "externalId",
            CancellationToken.None);

        var mixed = await testDatabase.Loader.UpsertBatchAsync(
            [
                Row(2, ("externalId", "A"), ("sourceId", "changed"), ("name", "Augusta")),
                Row(3, ("externalId", "B"), ("sourceId", "old"), ("name", "Grace"))
            ],
            target,
            "externalId",
            CancellationToken.None);
        var collection = testDatabase.Database.GetCollection<BsonDocument>(collectionName);
        var updated = await collection.Find(new BsonDocument("externalId", "A")).SingleAsync();

        Assert.Equal((1L, 1L), (mixed.InsertedRows, mixed.UpdatedRows));
        Assert.Equal(2, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal("changed", updated["sourceId"].AsString);
        Assert.Equal("Augusta", updated["name"].AsString);
    }

    [Fact]
    public async Task BulkWrite_AllowsIdOnlyWhenItIsTheConfiguredIdentity()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var collectionName = $"rows_{Guid.NewGuid():N}";
        var target = new MongoTarget(testDatabase.DatabaseName, collectionName);

        var first = await testDatabase.Loader.UpsertBatchAsync(
            [Row(2, ("_id", "A"), ("name", "Ada"))],
            target,
            "_id",
            CancellationToken.None);
        var rerun = await testDatabase.Loader.UpsertBatchAsync(
            [Row(2, ("_id", "A"), ("name", "Updated"))],
            target,
            "_id",
            CancellationToken.None);

        Assert.Equal((1L, 0L), (first.InsertedRows, first.UpdatedRows));
        Assert.Equal((0L, 1L), (rerun.InsertedRows, rerun.UpdatedRows));
    }

    [Fact]
    public async Task BulkWrite_UsesSimpleIdentityAgainstCaseInsensitiveCollectionDefault()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var collectionName = $"rows_{Guid.NewGuid():N}";
        await testDatabase.Database.CreateCollectionAsync(
            collectionName,
            new CreateCollectionOptions
            {
                Collation = new Collation(
                    "en",
                    strength: CollationStrength.Secondary)
            });
        var target = new MongoTarget(testDatabase.DatabaseName, collectionName);

        var result = await testDatabase.Loader.UpsertBatchAsync(
            [
                Row(2, ("externalId", "A"), ("name", "upper")),
                Row(3, ("externalId", "a"), ("name", "lower"))
            ],
            target,
            "externalId",
            CancellationToken.None);
        var collection = testDatabase.Database.GetCollection<BsonDocument>(collectionName);

        Assert.Equal((2L, 0L), (result.InsertedRows, result.UpdatedRows));
        Assert.Equal(2, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(
            2,
            await collection.CountDocumentsAsync(new BsonDocument("externalId", "A")));
    }

    private static DataRow Row(
        long sourceRowNumber,
        params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };
        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }
}
