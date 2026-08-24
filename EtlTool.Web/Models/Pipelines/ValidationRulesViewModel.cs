using EtlTool.Domain.Entities;

namespace EtlTool.Web.Models.Pipelines;

public sealed class ValidationRulesViewModel
{
    public Guid PipelineId { get; init; }

    public IReadOnlyList<ValidationRule> Rules { get; init; } = [];
}
