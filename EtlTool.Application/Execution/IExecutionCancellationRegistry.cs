namespace EtlTool.Application.Execution;

public interface IExecutionCancellationRegistry
{
    IExecutionCancellationRegistration Register(Guid runId);

    bool TryRequestCancellation(Guid runId);
}
