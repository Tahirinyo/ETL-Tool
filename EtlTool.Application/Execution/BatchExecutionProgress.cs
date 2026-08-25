namespace EtlTool.Application.Execution;

public sealed class BatchExecutionProgress
{
    internal BatchExecutionProgress(
        long processedRows,
        long validRows,
        long invalidRows,
        long filteredRows,
        long deduplicatedRows,
        bool isCompleted)
    {
        if (processedRows < 0 ||
            validRows < 0 ||
            invalidRows < 0 ||
            filteredRows < 0 ||
            deduplicatedRows < 0 ||
            processedRows != validRows + invalidRows + filteredRows + deduplicatedRows)
        {
            throw new ArgumentException("The execution progress counters are inconsistent.");
        }

        ProcessedRows = processedRows;
        ValidRows = validRows;
        InvalidRows = invalidRows;
        FilteredRows = filteredRows;
        DeduplicatedRows = deduplicatedRows;
        IsCompleted = isCompleted;
    }

    public long ProcessedRows { get; }

    public long ValidRows { get; }

    public long InvalidRows { get; }

    public long FilteredRows { get; }

    public long DeduplicatedRows { get; }

    public bool IsCompleted { get; }
}
