using EtlTool.Domain.Enums;

namespace EtlTool.Application.Processing;

public sealed record RowProcessingError(
    RowProcessingErrorStage Stage,
    string? Field,
    string Message,
    Guid? RuleId = null,
    TransformationType? TransformationType = null);
