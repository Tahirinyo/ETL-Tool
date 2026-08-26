namespace EtlTool.Application.Loading;

public sealed record BatchLoadResult
{
    public static BatchLoadResult Empty { get; } = new(0, 0);

    public BatchLoadResult(long insertedRows, long updatedRows)
    {
        if (insertedRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(insertedRows));
        }

        if (updatedRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedRows));
        }

        InsertedRows = insertedRows;
        UpdatedRows = updatedRows;
    }

    public long InsertedRows { get; }

    public long UpdatedRows { get; }

    public bool HasCommittedRows => InsertedRows > 0 || UpdatedRows > 0;
}
