using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Loading;

public sealed class DataLoaderResolverTests
{
    [Fact]
    public void Resolve_ReturnsTheExplicitlyRegisteredMongoDbLoader()
    {
        var loader = new StubLoader(DestinationType.MongoDb);
        var resolver = new DataLoaderResolver([loader]);

        Assert.Same(loader, resolver.Resolve(DestinationType.MongoDb));
    }

    [Fact]
    public void Resolve_ReturnsEachExplicitlyRegisteredDestinationLoader()
    {
        var mongo = new StubLoader(DestinationType.MongoDb);
        var postgreSql = new StubLoader(DestinationType.PostgreSql);
        var resolver = new DataLoaderResolver([mongo, postgreSql]);

        Assert.Same(mongo, resolver.Resolve(DestinationType.MongoDb));
        Assert.Same(postgreSql, resolver.Resolve(DestinationType.PostgreSql));
    }

    [Fact]
    public void Constructor_RejectsInvalidNullAndDuplicateRegistrations()
    {
        Assert.Throws<ArgumentNullException>(() => new DataLoaderResolver(null!));
        Assert.Throws<ArgumentException>(() =>
            new DataLoaderResolver(new IDataLoader[] { null! }));
        Assert.Throws<InvalidOperationException>(() =>
            new DataLoaderResolver([new StubLoader(DestinationType.Unspecified)]));
        Assert.Throws<InvalidOperationException>(() =>
            new DataLoaderResolver([new StubLoader((DestinationType)999)]));
        Assert.Throws<InvalidOperationException>(() =>
            new DataLoaderResolver(
            [
                new StubLoader(DestinationType.MongoDb),
                new StubLoader(DestinationType.MongoDb)
            ]));
    }

    [Fact]
    public void Resolve_RejectsInvalidAndUnregisteredTypesWithoutMongoDbFallback()
    {
        var mongo = new StubLoader(DestinationType.MongoDb);
        var resolver = new DataLoaderResolver([mongo]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            resolver.Resolve(DestinationType.Unspecified));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            resolver.Resolve((DestinationType)999));
        Assert.Throws<KeyNotFoundException>(() =>
            resolver.Resolve(DestinationType.PostgreSql));
    }

    private sealed class StubLoader(DestinationType destinationType) : IDataLoader
    {
        public DestinationType DestinationType { get; } = destinationType;

        public Task PrepareAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BatchLoadResult> UpsertBatchAsync(
            IReadOnlyList<DataRow> rows,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken) => Task.FromResult(BatchLoadResult.Empty);
    }
}
