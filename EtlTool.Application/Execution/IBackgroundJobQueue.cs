namespace EtlTool.Application.Execution;

public interface IBackgroundJobQueue
{
    ValueTask EnqueueAsync(
        BackgroundJob job,
        CancellationToken cancellationToken);
}
