namespace EtlTool.Application.Execution;

public sealed class BatchExecutionResult
{
    internal BatchExecutionResult(
        long processedRows,
        long validRows,
        long invalidRows,
        long filteredRows,
        long deduplicatedRows,
        long insertedRows = 0,
        long updatedRows = 0)
    {
        ProcessedRows = processedRows;
        ValidRows = validRows;
        InvalidRows = invalidRows;
        FilteredRows = filteredRows;
        DeduplicatedRows = deduplicatedRows;
        InsertedRows = insertedRows;
        UpdatedRows = updatedRows;
    }

    public long ProcessedRows { get; }

    public long ValidRows { get; }

    public long InvalidRows { get; }

    public long FilteredRows { get; }

    public long DeduplicatedRows { get; }

    public long InsertedRows { get; }

    public long UpdatedRows { get; }
}
