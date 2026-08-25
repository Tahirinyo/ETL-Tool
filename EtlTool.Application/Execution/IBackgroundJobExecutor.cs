namespace EtlTool.Application.Execution;

public interface IBackgroundJobExecutor
{
    Task ExecuteAsync(
        BackgroundJob job,
        CancellationToken cancellationToken);
}
