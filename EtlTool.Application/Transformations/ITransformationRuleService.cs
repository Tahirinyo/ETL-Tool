using EtlTool.Domain.Entities;

namespace EtlTool.Application.Transformations;

public interface ITransformationRuleService
{
    Task<TransformationRule?> CreateAsync(
        Guid pipelineId,
        TransformationRuleInput input,
        CancellationToken cancellationToken);

    Task<bool> UpdateAsync(
        Guid pipelineId,
        Guid ruleId,
        TransformationRuleInput input,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        Guid pipelineId,
        Guid ruleId,
        CancellationToken cancellationToken);

    Task<bool> ReorderAsync(
        Guid pipelineId,
        IReadOnlyList<Guid> orderedRuleIds,
        CancellationToken cancellationToken);
}
