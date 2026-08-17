using EtlTool.Domain.Enums;

namespace EtlTool.Domain.Entities;

public sealed class ValidationRule
{
    public Guid Id { get; set; }

    public ValidationType Type { get; set; }

    public string Field { get; set; } = string.Empty;

    public Dictionary<string, string> Configuration { get; set; } = new(StringComparer.Ordinal);

    public string ErrorMessage { get; set; } = string.Empty;
}
