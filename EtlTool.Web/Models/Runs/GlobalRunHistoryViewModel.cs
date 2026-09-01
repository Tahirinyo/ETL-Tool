namespace EtlTool.Web.Models.Runs;

public sealed class GlobalRunHistoryViewModel
{
    public IReadOnlyList<GlobalRunHistoryItemViewModel> Runs { get; init; } = [];
}

public sealed class GlobalRunHistoryItemViewModel
{
    public Guid PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public DateTimeOffset? StartedAt { get; init; }
    public RunHistoryItemViewModel Run { get; init; } = new();
}
