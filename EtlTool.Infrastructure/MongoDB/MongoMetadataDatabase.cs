using EtlTool.Domain.Entities;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoMetadataDatabase
{
    private readonly IMongoClient _client;
    private readonly IMongoDatabase _database;

    public MongoMetadataDatabase(MongoDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        MongoBsonMappings.Register();

        _client = new MongoClient(options.ConnectionString);
        _database = _client.GetDatabase(options.MetadataDatabaseName);
    }

    internal IMongoCollection<PipelineDefinition> PipelineDefinitions =>
        _database.GetCollection<PipelineDefinition>(
            MongoMetadataCollectionNames.PipelineDefinitions);

    internal IMongoCollection<EtlRun> EtlRuns =>
        _database.GetCollection<EtlRun>(MongoMetadataCollectionNames.EtlRuns);

    internal IMongoDatabase GetDatabase(string databaseName) =>
        _client.GetDatabase(databaseName);

    internal Task<IAsyncCursor<string>> ListDatabaseNamesAsync(
        CancellationToken cancellationToken) =>
        _client.ListDatabaseNamesAsync(cancellationToken);
}
