using EtlTool.Domain.Entities;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoMetadataDatabase
{
    private readonly IMongoDatabase _database;

    public MongoMetadataDatabase(MongoDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        MongoBsonMappings.Register();

        var client = new MongoClient(options.ConnectionString);
        _database = client.GetDatabase(options.MetadataDatabaseName);
    }

    internal IMongoCollection<PipelineDefinition> PipelineDefinitions =>
        _database.GetCollection<PipelineDefinition>(
            MongoMetadataCollectionNames.PipelineDefinitions);
}
