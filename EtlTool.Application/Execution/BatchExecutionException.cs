using EtlTool.Application.Loading;

namespace EtlTool.Application.Execution;

public sealed class BatchExecutionException : Exception
{
    internal BatchExecutionException(
        BatchExecutionProgress confirmedProgress,
        BatchLoadException loadFailure)
        : base("Batch execution failed after the destination reported confirmed write results.", loadFailure)
    {
        ConfirmedProgress = confirmedProgress;
    }

    public BatchExecutionProgress ConfirmedProgress { get; }
}
