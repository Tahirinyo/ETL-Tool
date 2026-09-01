using EtlTool.Domain.Entities;

namespace EtlTool.Application.Pipelines;

public sealed class PipelineService : IPipelineService
{
    private readonly IPipelineDefinitionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public PipelineService(
        IPipelineDefinitionRepository repository,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async Task<PipelineDefinition> CreateAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ValidatePipeline(pipeline);
        ClearAdmittedConnectionRevisions(pipeline);

        var now = _timeProvider.GetUtcNow();
        pipeline.Id = Guid.NewGuid();
        pipeline.CreatedAt = now;
        pipeline.UpdatedAt = now;

        await _repository.AddAsync(pipeline, cancellationToken);

        return pipeline;
    }

    public Task<PipelineDefinition?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        return _repository.GetByIdAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<PipelineDefinition>> ListAsync(
        CancellationToken cancellationToken)
    {
        return _repository.ListAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        Guid id,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        ValidatePipeline(pipeline);
        ClearAdmittedConnectionRevisions(pipeline);

        if (pipeline.Id != Guid.Empty && pipeline.Id != id)
        {
            throw new ArgumentException(
                "Pipeline identifier must match the update target identifier.",
                nameof(pipeline));
        }

        var existing = await _repository.GetByIdAsync(id, cancellationToken);

        if (existing is null)
        {
            return false;
        }

        pipeline.Id = id;
        pipeline.CreatedAt = existing.CreatedAt;
        pipeline.UpdatedAt = _timeProvider.GetUtcNow();

        return await _repository.UpdateAsync(pipeline, cancellationToken);
    }

    public Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        return _repository.DeleteAsync(id, cancellationToken);
    }

    private static void ValidatePipeline(PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        if (string.IsNullOrWhiteSpace(pipeline.Name))
        {
            throw new ArgumentException("Pipeline name cannot be empty.", nameof(pipeline));
        }
    }

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(id));
        }
    }

    private static void ClearAdmittedConnectionRevisions(PipelineDefinition pipeline)
    {
        if (pipeline.PostgreSqlSource is not null)
        {
            pipeline.PostgreSqlSource.SavedConnectionRevision = null;
        }
        if (pipeline.MongoDbSource is not null)
        {
            pipeline.MongoDbSource.SavedConnectionRevision = null;
        }
        if (pipeline.PostgreSqlDestination is not null)
        {
            pipeline.PostgreSqlDestination.SavedConnectionRevision = null;
        }
        pipeline.MongoDbDestinationConnectionRevision = null;
    }
}
