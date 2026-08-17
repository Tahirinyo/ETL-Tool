using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoPipelineDefinitionRepository : IPipelineDefinitionRepository
{
    private readonly IMongoCollection<PipelineDefinition> _collection;

    public MongoPipelineDefinitionRepository(MongoMetadataDatabase metadataDatabase)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        _collection = metadataDatabase.PipelineDefinitions;
    }

    public async Task AddAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ValidatePipeline(pipeline);

        try
        {
            await _collection.InsertOneAsync(
                pipeline,
                options: null,
                cancellationToken);
        }
        catch (MongoWriteException exception) when (IsDuplicateIdWrite(exception))
        {
            throw new DuplicatePipelineDefinitionException(pipeline.Id, exception);
        }
    }

    public async Task<PipelineDefinition?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);

        return await _collection
            .Find(pipeline => pipeline.Id == id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PipelineDefinition>> ListAsync(
        CancellationToken cancellationToken)
    {
        return await _collection
            .Find(Builders<PipelineDefinition>.Filter.Empty)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ValidatePipeline(pipeline);

        var result = await _collection.ReplaceOneAsync(
            persisted => persisted.Id == pipeline.Id,
            pipeline,
            new ReplaceOptions { IsUpsert = false },
            cancellationToken);

        return result.MatchedCount > 0;
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);

        var result = await _collection.DeleteOneAsync(
            pipeline => pipeline.Id == id,
            cancellationToken);

        return result.DeletedCount > 0;
    }

    private static bool IsDuplicateIdWrite(MongoWriteException exception)
    {
        var writeError = exception.WriteError;

        if (writeError.Category != ServerErrorCategory.DuplicateKey)
        {
            return false;
        }

        var details = writeError.Details;

        var hasIdKeyPattern = details is not null
            && details.TryGetValue("keyPattern", out var keyPattern)
            && keyPattern.BsonType == BsonType.Document
            && keyPattern.AsBsonDocument.Contains("_id");

        return hasIdKeyPattern
            || writeError.Message.Contains(
                " index: _id_ dup key:",
                StringComparison.Ordinal);
    }

    private static void ValidatePipeline(PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ValidateId(pipeline.Id);
    }

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(id));
        }
    }
}
