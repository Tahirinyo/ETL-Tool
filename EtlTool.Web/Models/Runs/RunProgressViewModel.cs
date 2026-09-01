namespace EtlTool.Web.Models.Runs;

public sealed class RunProgressViewModel
{
    public Guid RunId { get; init; }
    public Guid? PipelineId { get; init; }
}
