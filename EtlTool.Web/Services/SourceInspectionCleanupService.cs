using EtlTool.Infrastructure.Sources;

namespace EtlTool.Web.Services;

public sealed class SourceInspectionCleanupService(
    SourceInspectionService inspections,
    TimeProvider timeProvider,
    ILogger<SourceInspectionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RunCleanupOperationAsync(
                () => inspections.PurgeOrphanedUploadsAsync(cancellationToken),
                "startup orphan upload cleanup")
            .ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunCleanupOperationAsync(
                        inspections.PurgeExpiredAsync,
                        "expired staged upload cleanup")
                    .ConfigureAwait(false);
                await RunCleanupOperationAsync(
                        () => inspections.PurgeOrphanedUploadsAsync(stoppingToken),
                        "orphan upload cleanup")
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the timer wait.
        }
    }

    private async Task RunCleanupOperationAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Temporary-file {CleanupOperation} could not complete and will be retried.", operationName);
        }
    }
}
