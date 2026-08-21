namespace EtlTool.Web.Models.Pipelines;

public sealed class TransformationRulesViewModel
{
    public Guid PipelineId { get; init; }

    public IReadOnlyList<TransformationRuleCardViewModel> Rules { get; init; } = [];
}
