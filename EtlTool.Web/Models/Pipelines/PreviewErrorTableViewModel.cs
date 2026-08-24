using System.Collections.ObjectModel;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;

namespace EtlTool.Web.Models.Pipelines;

public sealed class PreviewErrorTableViewModel
{
    private PreviewErrorTableViewModel(IReadOnlyList<PreviewErrorRowViewModel> errors)
    {
        Errors = errors;
    }

    public IReadOnlyList<PreviewErrorRowViewModel> Errors { get; }

    public static PreviewErrorTableViewModel FromPreview(PreviewResult preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var errors = preview.Rows
            .Where(row => row.Status == RowProcessingStatus.Invalid)
            .SelectMany(row => row.Errors.Select(error => new PreviewErrorRowViewModel(
                row.Row.SourceRowNumber,
                error.Field,
                error.Stage,
                error.Message)))
            .ToArray();

        return new PreviewErrorTableViewModel(
            new ReadOnlyCollection<PreviewErrorRowViewModel>(errors));
    }
}

public sealed record PreviewErrorRowViewModel(
    long SourceRowNumber,
    string? Field,
    RowProcessingErrorStage Stage,
    string Message);
