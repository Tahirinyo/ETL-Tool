namespace EtlTool.Application.Execution;

public sealed class BatchExecutionCanceledException : OperationCanceledException
{
    internal BatchExecutionCanceledException(
        BatchExecutionProgress confirmedProgress,
        OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : base("Batch execution was cancelled.", innerException, cancellationToken)
    {
        ConfirmedProgress = confirmedProgress;
    }

    public BatchExecutionProgress ConfirmedProgress { get; }
}
