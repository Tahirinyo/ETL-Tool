using EtlTool.Domain.Enums;

namespace EtlTool.Domain.ValueObjects;

public sealed class SourceFieldDefinition
{
    public string Name { get; set; } = string.Empty;

    public SourceFieldType DataType { get; set; }
}
