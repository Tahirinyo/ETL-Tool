namespace EtlTool.Web.Models.Runs;

public sealed class RunHistoryViewModel
{
    public Guid PipelineId { get; init; }

    public string PipelineName { get; init; } = string.Empty;

    public IReadOnlyList<RunHistoryItemViewModel> Runs { get; init; } = [];
}
