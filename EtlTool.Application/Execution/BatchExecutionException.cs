namespace EtlTool.Application.Execution;

public sealed class BatchExecutionException : Exception
{
    internal BatchExecutionException(
        BatchExecutionProgress confirmedProgress,
        Exception executionFailure)
        : base("Batch execution failed after processing started.", executionFailure)
    {
        ArgumentNullException.ThrowIfNull(confirmedProgress);
        ArgumentNullException.ThrowIfNull(executionFailure);

        ConfirmedProgress = confirmedProgress;
        ExecutionFailure = executionFailure;
    }

    public BatchExecutionProgress ConfirmedProgress { get; }

    public Exception ExecutionFailure { get; }
}
