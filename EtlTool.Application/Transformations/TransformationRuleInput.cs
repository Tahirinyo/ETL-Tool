using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed record TransformationRuleInput(
    TransformationType Type,
    string? SourceField,
    string? DefaultValue,
    string? Find,
    string? Replace);
