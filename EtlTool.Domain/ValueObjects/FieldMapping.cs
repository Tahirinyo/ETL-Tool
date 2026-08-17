namespace EtlTool.Domain.ValueObjects;

public sealed class FieldMapping
{
    public string SourceField { get; set; } = string.Empty;

    public string TargetField { get; set; } = string.Empty;

    public bool IsIncluded { get; set; } = true;
}
