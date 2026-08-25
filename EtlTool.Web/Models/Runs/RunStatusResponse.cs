using EtlTool.Domain.Entities;

namespace EtlTool.Web.Models.Runs;

public sealed class RunStatusResponse
{
    public Guid RunId { get; init; }

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public long TotalRows { get; init; }

    public long ProcessedRows { get; init; }

    public long ValidRows { get; init; }

    public long InvalidRows { get; init; }

    public long FilteredRows { get; init; }

    public long DeduplicatedRows { get; init; }

    public long InsertedRows { get; init; }

    public long UpdatedRows { get; init; }

    public static RunStatusResponse From(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new RunStatusResponse
        {
            RunId = run.Id,
            Status = run.Status.ToString(),
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            TotalRows = run.TotalRows,
            ProcessedRows = run.ProcessedRows,
            ValidRows = run.ValidRows,
            InvalidRows = run.InvalidRows,
            FilteredRows = run.FilteredRows,
            DeduplicatedRows = run.DeduplicatedRows,
            InsertedRows = run.InsertedRows,
            UpdatedRows = run.UpdatedRows
        };
    }
}
