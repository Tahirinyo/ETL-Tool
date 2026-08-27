using EtlTool.Domain.Enums;

namespace EtlTool.Domain.Entities;

public sealed class EtlRun
{
    public Guid Id { get; set; }

    public Guid PipelineId { get; set; }

    public string PipelineName { get; set; } = string.Empty;

    public EtlRunStatus Status { get; set; } = EtlRunStatus.Queued;

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFilePath { get; set; } = string.Empty;

    public EtlRunExecutionConfiguration? ExecutionConfiguration { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long TotalRows { get; set; }

    public long ProcessedRows { get; set; }

    public long ValidRows { get; set; }

    public long InvalidRows { get; set; }

    public long FilteredRows { get; set; }

    public long DeduplicatedRows { get; set; }

    public long InsertedRows { get; set; }

    public long UpdatedRows { get; set; }

    public string? SystemError { get; set; }

    public string? ErrorReportPath { get; set; }
}
