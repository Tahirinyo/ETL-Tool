using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed record ValidationRuleInput(
    ValidationType Type,
    string? Field,
    string? Minimum,
    string? Maximum,
    string? ErrorMessage);
