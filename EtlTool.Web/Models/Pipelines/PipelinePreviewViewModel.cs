using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Domain.Entities;

namespace EtlTool.Web.Models.Pipelines;

public sealed class PipelinePreviewViewModel
{
    public Guid PipelineId { get; init; }

    public string PipelineName { get; init; } = string.Empty;

    public int ValidRowCount { get; init; }

    public int InvalidRowCount { get; init; }

    public int FilteredRowCount { get; init; }

    public PreviewTableViewModel? Table { get; init; }

    public PreviewErrorTableViewModel? Errors { get; init; }

    public IReadOnlyList<PipelineReadinessProblem> ReadinessProblems { get; init; } = [];

    public string? FailureMessage { get; init; }

    public bool RequiresSourceUpload { get; init; }

    public bool HasPreview => Table is not null && Errors is not null;

    public static PipelinePreviewViewModel FromPreview(
        PipelineDefinition pipeline,
        PreviewResult preview)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(preview);

        return new PipelinePreviewViewModel
        {
            PipelineId = pipeline.Id,
            PipelineName = pipeline.Name,
            ValidRowCount = preview.ValidRowCount,
            InvalidRowCount = preview.InvalidRowCount,
            FilteredRowCount = preview.FilteredRowCount,
            Table = PreviewTableViewModel.FromPreview(preview, pipeline),
            Errors = PreviewErrorTableViewModel.FromPreview(preview)
        };
    }
}
