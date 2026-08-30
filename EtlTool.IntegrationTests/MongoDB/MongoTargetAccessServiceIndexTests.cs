using System.Security.Cryptography;
using System.Text;
using EtlTool.Application.MongoDB;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoTargetAccessServiceIndexTests(MongoDbFixture fixture)
{
    private readonly MongoDbFixture _fixture = fixture;

    [Fact]
    public async Task EnsureUpsertIndexAsync_ReusesSuitableSingleFieldIndex()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "existing_single");
        var collection = Collection(database, target);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Descending("customerId"),
            new CreateIndexOptions
            {
                Name = "existing_customer_id",
                Unique = true,
                Collation = Collation.Simple
            }));
        var before = await ListIndexesAsync(collection);

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);

        var after = await ListIndexesAsync(collection);
        Assert.Equal(before.Count, after.Count);
        Assert.Contains(after, index => index["name"] == "existing_customer_id");
        Assert.DoesNotContain(after, index => index["name"] == "customerId_1");
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_ReusesSuitableCompoundPrefixIndex()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "existing_compound");
        var collection = Collection(database, target);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys
                .Ascending("customerId")
                .Descending("region"),
            new CreateIndexOptions
            {
                Name = "existing_customer_region",
                Collation = Collation.Simple
            }));

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);

        var indexes = await ListIndexesAsync(collection);
        Assert.Equal(2, indexes.Count);
        Assert.Contains(indexes, index => index["name"] == "existing_customer_region");
        Assert.DoesNotContain(indexes, index => index["name"] == "customerId_1");
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_CreatesCompatibleIndexOnExistingCollection()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "existing_without_index");
        var collection = Collection(database, target);
        await collection.InsertOneAsync(new BsonDocument("name", "Ada"));

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);

        AssertCompatibleCreatedIndex(await ListIndexesAsync(collection), "customerId");
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_CreatesCollectionAndIndexForNewTarget()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "new_collection");
        var targetDatabase = database.Client.GetDatabase(target.DatabaseName);
        Assert.DoesNotContain(target.CollectionName, await ListCollectionNamesAsync(targetDatabase));

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);

        Assert.Contains(target.CollectionName, await ListCollectionNamesAsync(targetDatabase));
        AssertCompatibleCreatedIndex(
            await ListIndexesAsync(Collection(database, target)),
            "customerId");
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_IsStableAcrossRepeatedCalls()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "repeated_readiness");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await database.TargetAccessService.EnsureUpsertIndexAsync(
                target,
                "customerId",
                CancellationToken.None);
        }

        var indexes = await ListIndexesAsync(Collection(database, target));
        Assert.Equal(2, indexes.Count);
        Assert.Single(indexes, index => index["name"] == "customerId_1");
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_AddsNewKeyWithoutRemovingOldKeyIndex()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "changed_key");

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);
        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "externalId",
            CancellationToken.None);

        var indexes = await ListIndexesAsync(Collection(database, target));
        Assert.Contains(indexes, index => index["name"] == "customerId_1");
        Assert.Contains(indexes, index => index["name"] == "externalId_1");
        Assert.Equal(3, indexes.Count);
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_HandlesConcurrentEquivalentReadiness()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "concurrent_readiness");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            database.TargetAccessService.EnsureUpsertIndexAsync(
                target,
                "customerId",
                CancellationToken.None)));

        var indexes = await ListIndexesAsync(Collection(database, target));
        Assert.Equal(2, indexes.Count);
        Assert.Single(indexes, index => index["name"] == "customerId_1");
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_DoesNotReuseIncompatibleIndexes()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "incompatible_indexes");
        var collection = Collection(database, target);
        var keys = Builders<BsonDocument>.IndexKeys;
        await collection.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                keys.Ascending("customerId").Ascending("sparseTail"),
                new CreateIndexOptions { Name = "sparse", Sparse = true }),
            new CreateIndexModel<BsonDocument>(
                keys.Ascending("customerId").Ascending("partialTail"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "partial",
                    PartialFilterExpression = Builders<BsonDocument>.Filter.Exists("customerId")
                }),
            new CreateIndexModel<BsonDocument>(
                keys.Ascending("customerId").Ascending("hiddenTail"),
                new CreateIndexOptions { Name = "hidden", Hidden = true }),
            new CreateIndexModel<BsonDocument>(
                keys.Ascending("customerId"),
                new CreateIndexOptions { Name = "non_simple", Collation = new Collation("en") }),
            new CreateIndexModel<BsonDocument>(
                keys.Ascending("other").Ascending("customerId"),
                new CreateIndexOptions { Name = "wrong_prefix", Collation = Collation.Simple }),
            new CreateIndexModel<BsonDocument>(
                new BsonDocumentIndexKeysDefinition<BsonDocument>(new BsonDocument("customerId", "hashed")),
                new CreateIndexOptions { Name = "hashed" })
        ]);

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);

        var indexes = await ListIndexesAsync(collection);
        AssertCompatibleCreatedIndex(indexes, "customerId");
        Assert.Equal(8, indexes.Count);
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_UsesFallbackNameWhenDefaultNameIsOccupiedBySparseIndex()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "default_name_conflict");
        var collection = Collection(database, target);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("customerId"),
            new CreateIndexOptions { Sparse = true }));
        var expectedFallbackName = FallbackIndexName("customerId");

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);
        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);

        var indexes = await ListIndexesAsync(collection);
        var original = Assert.Single(indexes, index => index["name"] == "customerId_1");
        Assert.True(original["sparse"].AsBoolean);
        AssertCompatibleCreatedIndex(indexes, "customerId", expectedFallbackName);
        Assert.Equal(3, indexes.Count);
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_ConcurrentFallbackCallsCreateOneCompatibleIndex()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "concurrent_default_name_conflict");
        var collection = Collection(database, target);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("customerId"),
            new CreateIndexOptions { Sparse = true }));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            database.TargetAccessService.EnsureUpsertIndexAsync(
                target,
                "customerId",
                CancellationToken.None)));

        var indexes = await ListIndexesAsync(collection);
        var original = Assert.Single(indexes, index => index["name"] == "customerId_1");
        Assert.True(original["sparse"].AsBoolean);
        AssertCompatibleCreatedIndex(indexes, "customerId", FallbackIndexName("customerId"));
        Assert.Equal(3, indexes.Count);
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_DoesNotReuseIndexWithCollectionDefaultCollation()
    {
        await using var database = _fixture.CreateDatabase();
        var target = Target(database, "default_collation");
        var targetDatabase = database.Client.GetDatabase(target.DatabaseName);
        await targetDatabase.CreateCollectionAsync(
            target.CollectionName,
            new CreateCollectionOptions { Collation = new Collation("en") });
        var collection = Collection(database, target);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("customerId"),
            new CreateIndexOptions { Name = "collection_default_collation" }));

        await database.TargetAccessService.EnsureUpsertIndexAsync(
            target,
            "customerId",
            CancellationToken.None);

        var indexes = await ListIndexesAsync(collection);
        AssertCompatibleCreatedIndex(indexes, "customerId");
        Assert.Equal("en", indexes.Single(index =>
            index["name"] == "collection_default_collation")["collation"]["locale"]);
    }

    [Fact]
    public async Task EnsureUpsertIndexAsync_PropagatesCancellation()
    {
        var options = new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_metadata"
        };
        var service = new MongoTargetAccessService(new MongoMetadataDatabase(options), options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnsureUpsertIndexAsync(
                new MongoTarget("etl_tool_target_safety_tests", "customers"),
                "customerId",
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static MongoTarget Target(MongoDbTestDatabase database, string collectionName) =>
        new($"{database.DatabaseName}_target", collectionName);

    private static IMongoCollection<BsonDocument> Collection(
        MongoDbTestDatabase database,
        MongoTarget target) =>
        database.Client
            .GetDatabase(target.DatabaseName)
            .GetCollection<BsonDocument>(target.CollectionName);

    private static async Task<List<BsonDocument>> ListIndexesAsync(
        IMongoCollection<BsonDocument> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static async Task<List<string>> ListCollectionNamesAsync(IMongoDatabase database)
    {
        using var cursor = await database.ListCollectionNamesAsync();
        return await cursor.ToListAsync();
    }

    private static void AssertCompatibleCreatedIndex(
        IReadOnlyList<BsonDocument> indexes,
        string upsertKeyField,
        string? indexName = null)
    {
        var index = Assert.Single(
            indexes,
            item => item["name"] == (indexName ?? $"{upsertKeyField}_1"));
        Assert.Equal(new BsonDocument(upsertKeyField, 1), index["key"].AsBsonDocument);
        Assert.False(index.TryGetValue("unique", out var unique) && unique.AsBoolean);
        Assert.False(index.TryGetValue("sparse", out var sparse) && sparse.AsBoolean);
        Assert.False(index.Contains("partialFilterExpression"));
        Assert.False(index.TryGetValue("hidden", out var hidden) && hidden.AsBoolean);
        Assert.True(
            !index.TryGetValue("collation", out var collation)
            || collation["locale"] == "simple");
    }

    private static string FallbackIndexName(string upsertKeyField)
    {
        var fieldHash = SHA256.HashData(Encoding.UTF8.GetBytes(upsertKeyField));
        return $"etl_upsert_{Convert.ToHexString(fieldHash).ToLowerInvariant()}";
    }
}
