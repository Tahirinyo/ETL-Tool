namespace EtlTool.Application.Execution;

public sealed class DuplicateEtlRunException : Exception
{
    public DuplicateEtlRunException(Guid runId, Exception? innerException = null)
        : base($"An ETL run with identifier '{runId}' already exists.", innerException)
    {
        RunId = runId;
    }

    public Guid RunId { get; }
}
