namespace EtlTool.Application.Execution;

public sealed class BatchExecutionProgress
{
    internal BatchExecutionProgress(
        long processedRows,
        long validRows,
        long invalidRows,
        long filteredRows,
        long deduplicatedRows,
        bool isCompleted,
        long insertedRows = 0,
        long updatedRows = 0)
    {
        if (processedRows < 0 ||
            validRows < 0 ||
            invalidRows < 0 ||
            filteredRows < 0 ||
            deduplicatedRows < 0 ||
            insertedRows < 0 ||
            updatedRows < 0 ||
            processedRows != validRows + invalidRows + filteredRows + deduplicatedRows ||
            insertedRows + updatedRows > validRows)
        {
            throw new ArgumentException("The execution progress counters are inconsistent.");
        }

        ProcessedRows = processedRows;
        ValidRows = validRows;
        InvalidRows = invalidRows;
        FilteredRows = filteredRows;
        DeduplicatedRows = deduplicatedRows;
        InsertedRows = insertedRows;
        UpdatedRows = updatedRows;
        IsCompleted = isCompleted;
    }

    public long ProcessedRows { get; }

    public long ValidRows { get; }

    public long InvalidRows { get; }

    public long FilteredRows { get; }

    public long DeduplicatedRows { get; }

    public long InsertedRows { get; }

    public long UpdatedRows { get; }

    public bool IsCompleted { get; }
}
