using System.Collections.ObjectModel;
using EtlTool.Application.Extraction;

namespace EtlTool.Application.Processing;

public sealed class RowProcessingResult
{
    private RowProcessingResult(
        DataRow row,
        RowProcessingStatus status,
        IReadOnlyList<RowProcessingError> errors)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(errors);

        Row = row;
        Status = status;
        var copiedErrors = errors.ToArray();
        if (copiedErrors.Any(error => error is null))
        {
            throw new ArgumentException(
                "The row-processing error collection contains an invalid error.",
                nameof(errors));
        }

        Errors = new ReadOnlyCollection<RowProcessingError>(copiedErrors);
    }

    public DataRow Row { get; }

    public RowProcessingStatus Status { get; }

    public IReadOnlyList<RowProcessingError> Errors { get; }

    internal static RowProcessingResult Valid(DataRow row) =>
        new(row, RowProcessingStatus.Valid, []);

    internal static RowProcessingResult Invalid(
        DataRow row,
        IReadOnlyList<RowProcessingError> errors)
    {
        if (errors is not { Count: > 0 })
        {
            throw new ArgumentException(
                "An invalid row-processing result requires at least one error.",
                nameof(errors));
        }

        return new RowProcessingResult(row, RowProcessingStatus.Invalid, errors);
    }

    internal static RowProcessingResult Filtered(DataRow row) =>
        new(row, RowProcessingStatus.Filtered, []);

    internal static RowProcessingResult Duplicate(DataRow row) =>
        new(row, RowProcessingStatus.Duplicate, []);
}
