using EtlTool.Domain.Entities;

namespace EtlTool.Application.Validations;

public interface IValidationRuleService
{
    Task<ValidationRule?> CreateAsync(
        Guid pipelineId,
        ValidationRuleInput input,
        CancellationToken cancellationToken);

    Task<bool> UpdateAsync(
        Guid pipelineId,
        Guid ruleId,
        ValidationRuleInput input,
        CancellationToken cancellationToken);
}
