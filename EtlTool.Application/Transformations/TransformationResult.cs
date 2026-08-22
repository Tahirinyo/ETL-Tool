using EtlTool.Application.Extraction;

namespace EtlTool.Application.Transformations;

public sealed class TransformationResult
{
    private TransformationResult(DataRow row, TransformationResultStatus status)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        Status = status;
    }

    public DataRow Row { get; }

    public TransformationResultStatus Status { get; }

    public bool IsFiltered => Status == TransformationResultStatus.Filtered;

    public bool IsDuplicate => Status == TransformationResultStatus.Duplicate;

    public static TransformationResult Transformed(DataRow row) =>
        new(row, TransformationResultStatus.Transformed);

    public static TransformationResult Filtered(DataRow row) =>
        new(row, TransformationResultStatus.Filtered);

    public static TransformationResult Duplicate(DataRow row) =>
        new(row, TransformationResultStatus.Duplicate);
}
