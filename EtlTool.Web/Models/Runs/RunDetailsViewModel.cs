using EtlTool.Domain.Entities;

namespace EtlTool.Web.Models.Runs;

public sealed class RunDetailsViewModel
{
    public Guid Id { get; init; }

    public Guid PipelineId { get; init; }

    public string PipelineName { get; init; } = string.Empty;

    public string OriginalFileName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string StartedAtDisplay { get; init; } = string.Empty;

    public string CompletedAtDisplay { get; init; } = string.Empty;

    public string DurationDisplay { get; init; } = string.Empty;

    public long TotalRows { get; init; }

    public long ProcessedRows { get; init; }

    public long ValidRows { get; init; }

    public long InvalidRows { get; init; }

    public long FilteredRows { get; init; }

    public long DeduplicatedRows { get; init; }

    public long InsertedRows { get; init; }

    public long UpdatedRows { get; init; }

    public string? SystemError { get; init; }

    public bool HasErrorReport { get; init; }

    public static RunDetailsViewModel From(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new RunDetailsViewModel
        {
            Id = run.Id,
            PipelineId = run.PipelineId,
            PipelineName = run.PipelineName,
            OriginalFileName = run.OriginalFileName,
            Status = run.Status.ToString(),
            StartedAtDisplay = RunDisplayFormatting.Timestamp(run.StartedAt, "Not started"),
            CompletedAtDisplay = RunDisplayFormatting.Timestamp(run.CompletedAt, "In progress"),
            DurationDisplay = RunDisplayFormatting.Duration(run.StartedAt, run.CompletedAt),
            TotalRows = run.TotalRows,
            ProcessedRows = run.ProcessedRows,
            ValidRows = run.ValidRows,
            InvalidRows = run.InvalidRows,
            FilteredRows = run.FilteredRows,
            DeduplicatedRows = run.DeduplicatedRows,
            InsertedRows = run.InsertedRows,
            UpdatedRows = run.UpdatedRows,
            SystemError = run.SystemError,
            HasErrorReport = !string.IsNullOrWhiteSpace(run.ErrorReportPath)
        };
    }
}
