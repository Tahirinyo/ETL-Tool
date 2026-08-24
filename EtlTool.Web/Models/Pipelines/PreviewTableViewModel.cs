using System.Collections.ObjectModel;
using System.Globalization;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;
using EtlTool.Domain.Entities;

namespace EtlTool.Web.Models.Pipelines;

public sealed class PreviewTableViewModel
{
    private PreviewTableViewModel(
        IReadOnlyList<string> columns,
        IReadOnlyList<PreviewTableRowViewModel> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<PreviewTableRowViewModel> Rows { get; }

    public static PreviewTableViewModel FromPreview(
        PreviewResult preview,
        PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(pipeline);

        var columns = pipeline.FieldMappings
            .Where(mapping => mapping.IsIncluded)
            .Select(mapping => mapping.TargetField)
            .ToArray();

        var rows = preview.Rows
            .Where(HasCompleteTransformedRow)
            .Select(row => new PreviewTableRowViewModel(
                row.Row.SourceRowNumber,
                new ReadOnlyCollection<PreviewTableCellViewModel>(columns
                    .Select(column => CreateCell(row.Row.Values, column))
                    .ToArray())))
            .ToArray();

        return new PreviewTableViewModel(
            new ReadOnlyCollection<string>(columns),
            new ReadOnlyCollection<PreviewTableRowViewModel>(rows));
    }

    private static bool HasCompleteTransformedRow(RowProcessingResult row) =>
        row.Status == RowProcessingStatus.Valid
        || (row.Status == RowProcessingStatus.Invalid
            && row.Errors.All(error => error.Stage == RowProcessingErrorStage.Validation));

    private static PreviewTableCellViewModel CreateCell(
        IReadOnlyDictionary<string, object?> values,
        string column)
    {
        if (!values.TryGetValue(column, out var value))
        {
            return new PreviewTableCellViewModel("Unavailable");
        }

        return new PreviewTableCellViewModel(FormatValue(value));
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "Null value",
        string { Length: 0 } => "Empty string (\"\")",
        string text when string.IsNullOrWhiteSpace(text) =>
            $"Whitespace-only value (length: {text.Length})",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "Unavailable"
    };
}

public sealed record PreviewTableRowViewModel(
    long SourceRowNumber,
    IReadOnlyList<PreviewTableCellViewModel> Cells);

public sealed record PreviewTableCellViewModel(string DisplayValue);
