namespace EtlTool.Application.Execution;

public interface IExecutionCancellationRegistration : IDisposable
{
    CancellationToken Token { get; }
}
