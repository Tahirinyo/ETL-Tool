using EtlTool.Application.MongoDB;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoSourceMetadataDiscoveryServiceFailureTests
{
    [Fact]
    public async Task DiscoverDatabasesAsync_UnreachableServerRaisesSafeAccessFailure()
    {
        var discovery = CreateUnreachableDiscoveryService();

        var exception = await Assert.ThrowsAsync<MongoSourceAccessException>(() =>
            discovery.DiscoverDatabasesAsync(CancellationToken.None));

        Assert.Equal("The configured MongoDB source could not be accessed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("mongodb://", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverDatabasesAsync_PropagatesCancellation()
    {
        var discovery = CreateUnreachableDiscoveryService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            discovery.DiscoverDatabasesAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("config")]
    [InlineData("local")]
    [InlineData("etl_tool_metadata")]
    [InlineData("bad/database")]
    public async Task DiscoverCollectionsAsync_RejectsDisallowedOrInvalidDatabase(string database)
    {
        var discovery = CreateUnreachableDiscoveryService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            discovery.DiscoverCollectionsAsync(database, CancellationToken.None));

        Assert.Equal("The selected MongoDB database cannot be used as an ETL source.", exception.Message);
    }

    private static MongoSourceMetadataDiscoveryService CreateUnreachableDiscoveryService()
    {
        var options = new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_metadata"
        };
        var metadataDatabase = new MongoMetadataDatabase(options);
        return new MongoSourceMetadataDiscoveryService(
            metadataDatabase,
            new MongoTargetAccessService(metadataDatabase, options));
    }
}

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoSourceMetadataDiscoveryServiceIntegrationTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task DiscoverDatabasesAndCollectionsAsync_ReturnsAllowedNamespacesOnly()
    {
        await using var metadata = fixture.CreateDatabase();
        var sourceDatabaseName = $"{metadata.DatabaseName}_source";
        var sourceDatabase = metadata.Client.GetDatabase(sourceDatabaseName);
        await sourceDatabase.CreateCollectionAsync("customers");
        await sourceDatabase.CreateCollectionAsync("orders");
        await metadata.Database.CreateCollectionAsync("pipeline_metadata");

        var discovery = CreateDiscoveryService(metadata);

        var databases = await discovery.DiscoverDatabasesAsync(CancellationToken.None);
        var collections = await discovery.DiscoverCollectionsAsync(
            sourceDatabaseName,
            CancellationToken.None);

        Assert.Contains(databases, database => database.Name == sourceDatabaseName);
        Assert.DoesNotContain(databases, database => database.Name == metadata.DatabaseName);
        Assert.DoesNotContain(databases, database => database.Name.Equals("admin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(databases, database => database.Name.Equals("config", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(databases, database => database.Name.Equals("local", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["customers", "orders"], collections.Select(collection => collection.Name));
    }

    [Fact]
    public async Task DiscoverCollectionsAsync_MissingDatabaseRaisesSafeNotFoundFailure()
    {
        await using var metadata = fixture.CreateDatabase();
        var discovery = CreateDiscoveryService(metadata);

        var exception = await Assert.ThrowsAsync<MongoSourceMetadataObjectNotFoundException>(() =>
            discovery.DiscoverCollectionsAsync(
                $"missing_{Guid.NewGuid():N}",
                CancellationToken.None));

        Assert.Equal("database", exception.ObjectType);
        Assert.Equal(
            "The selected MongoDB database was not found or is not accessible.",
            exception.Message);
    }

    private static MongoSourceMetadataDiscoveryService CreateDiscoveryService(
        MongoDbTestDatabase database)
    {
        var options = new MongoDbOptions
        {
            ConnectionString = database.ConnectionString,
            MetadataDatabaseName = database.DatabaseName
        };
        var metadataDatabase = new MongoMetadataDatabase(options);
        return new MongoSourceMetadataDiscoveryService(
            metadataDatabase,
            new MongoTargetAccessService(metadataDatabase, options));
    }
}
