namespace EtlTool.Web.Models.Pipelines;

public sealed class PipelineDeleteViewModel
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;
}
