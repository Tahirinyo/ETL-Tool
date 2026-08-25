namespace EtlTool.Application.Execution;

public sealed class BatchExecutionResult
{
    internal BatchExecutionResult(
        long processedRows,
        long validRows,
        long invalidRows,
        long filteredRows,
        long deduplicatedRows)
    {
        ProcessedRows = processedRows;
        ValidRows = validRows;
        InvalidRows = invalidRows;
        FilteredRows = filteredRows;
        DeduplicatedRows = deduplicatedRows;
    }

    public long ProcessedRows { get; }

    public long ValidRows { get; }

    public long InvalidRows { get; }

    public long FilteredRows { get; }

    public long DeduplicatedRows { get; }
}
