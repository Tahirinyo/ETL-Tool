using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.MongoDB;

public interface IMongoSourceSchemaInferenceService
{
    Task<IReadOnlyList<SourceFieldDefinition>> InferAsync(
        MongoDbSourceOptions source,
        CancellationToken cancellationToken);
}
