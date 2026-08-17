using EtlTool.Domain.Entities;

namespace EtlTool.Application.Pipelines;

public interface IPipelineService
{
    Task<PipelineDefinition> CreateAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);

    Task<PipelineDefinition?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PipelineDefinition>> ListAsync(
        CancellationToken cancellationToken);

    Task<bool> UpdateAsync(
        Guid id,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken);
}
