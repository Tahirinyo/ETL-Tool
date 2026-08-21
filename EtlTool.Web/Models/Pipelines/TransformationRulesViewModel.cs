using EtlTool.Domain.Entities;

namespace EtlTool.Web.Models.Pipelines;

public sealed class TransformationRulesViewModel
{
    public Guid PipelineId { get; init; }

    public IReadOnlyList<TransformationRule> Rules { get; init; } = [];
}
