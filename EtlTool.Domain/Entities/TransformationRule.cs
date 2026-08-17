using EtlTool.Domain.Enums;

namespace EtlTool.Domain.Entities;

public sealed class TransformationRule
{
    public Guid Id { get; set; }

    public TransformationType Type { get; set; }

    public int Order { get; set; }

    /// <summary>
    /// Gets or sets the logical field name available after field mapping has been applied.
    /// </summary>
    public string? SourceField { get; set; }

    public Dictionary<string, string> Configuration { get; set; } = new(StringComparer.Ordinal);
}
