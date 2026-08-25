using System.Collections.Concurrent;
using EtlTool.Application.Execution;
using EtlTool.Application.Pipelines;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace EtlTool.IntegrationTests.MongoDB;

[CollectionDefinition(CollectionName)]
public sealed class MongoDbTestCollection : ICollectionFixture<MongoDbFixture>
{
    public const string CollectionName = "MongoDB persistence";
}

public sealed class MongoDbFixture : IAsyncLifetime
{
    private const string Image = "mongo:8.0.28-noble";
    private readonly ConcurrentDictionary<string, byte> _databaseNames = new();
    private readonly MongoDbContainer _container = new MongoDbBuilder(Image).Build();
    private MongoClient? _cleanupClient;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _cleanupClient = new MongoClient(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_cleanupClient is not null)
            {
                foreach (var databaseName in _databaseNames.Keys)
                {
                    await _cleanupClient.DropDatabaseAsync(databaseName);
                }
            }
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }

    public MongoDbTestDatabase CreateDatabase()
    {
        var databaseName = $"etl_tool_metadata_tests_{Guid.NewGuid():N}";

        if (!_databaseNames.TryAdd(databaseName, 0))
        {
            throw new InvalidOperationException("Could not reserve a unique MongoDB test database name.");
        }

        return new MongoDbTestDatabase(this, _container.GetConnectionString(), databaseName);
    }

    internal async ValueTask DropDatabaseAsync(string databaseName)
    {
        if (!_databaseNames.ContainsKey(databaseName) || _cleanupClient is null)
        {
            return;
        }

        await _cleanupClient.DropDatabaseAsync(databaseName);
        _databaseNames.TryRemove(databaseName, out _);
    }
}

public sealed class MongoDbTestDatabase : IAsyncDisposable
{
    private readonly MongoDbFixture _fixture;

    internal MongoDbTestDatabase(
        MongoDbFixture fixture,
        string connectionString,
        string databaseName)
    {
        _fixture = fixture;
        DatabaseName = databaseName;
        Client = new MongoClient(connectionString);
        Database = Client.GetDatabase(databaseName);

        var metadataDatabase = new MongoMetadataDatabase(
            new MongoDbOptions
            {
                ConnectionString = connectionString,
                MetadataDatabaseName = databaseName
            });

        Repository = new MongoPipelineDefinitionRepository(metadataDatabase);
        EtlRunRepository = new MongoEtlRunRepository(metadataDatabase);
    }

    public string DatabaseName { get; }

    public MongoClient Client { get; }

    public IMongoDatabase Database { get; }

    public IPipelineDefinitionRepository Repository { get; }

    public IEtlRunRepository EtlRunRepository { get; }

    public ValueTask DisposeAsync()
    {
        return _fixture.DropDatabaseAsync(DatabaseName);
    }
}
