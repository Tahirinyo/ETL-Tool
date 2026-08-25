namespace EtlTool.Application.Execution;

public sealed class BackgroundJobQueueClosedException : InvalidOperationException
{
    public BackgroundJobQueueClosedException()
        : base("The background job queue is shutting down and cannot accept new work.")
    {
    }

    public BackgroundJobQueueClosedException(Exception innerException)
        : base("The background job queue is shutting down and cannot accept new work.", innerException)
    {
    }
}
