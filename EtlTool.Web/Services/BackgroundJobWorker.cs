using EtlTool.Application.Execution;
using EtlTool.Infrastructure.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace EtlTool.Web.Services;

public sealed class BackgroundJobWorker(
    InProcessBackgroundJobQueue queue,
    IServiceScopeFactory scopeFactory,
    IExecutionCancellationRegistry cancellationRegistry,
    AbandonedRunRecoveryService recoveryService,
    ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await recoveryService.RecoverStaleRunsAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (queue.IsAdmissionClosed || stoppingToken.IsCancellationRequested)
                {
                    await RecoverAbandonedAsync(job).ConfigureAwait(false);
                    break;
                }

                await ExecuteJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the channel wait or the active job.
        }
        finally
        {
            while (queue.TryRead(out var queuedJob))
            {
                await RecoverAbandonedAsync(queuedJob!).ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        queue.Complete();
        base.Dispose();
    }

    private async Task ExecuteJobAsync(
        BackgroundJob job,
        CancellationToken stoppingToken)
    {
        Exception? executionFailure = null;
        Exception? scopeDisposalFailure = null;
        var executionWasCancelled = false;

        IExecutionCancellationRegistration? registration = null;
        CancellationTokenSource? linkedCancellation = null;

        try
        {
            registration = cancellationRegistry.Register(job.RunId);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                registration.Token,
                stoppingToken);

            var scope = scopeFactory.CreateAsyncScope();
            try
            {
                var executor = scope.ServiceProvider.GetRequiredService<IBackgroundJobExecutor>();
                await executor.ExecuteAsync(job, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                executionFailure = exception;
                executionWasCancelled = exception is OperationCanceledException
                    && linkedCancellation.IsCancellationRequested;
            }
            finally
            {
                try
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    scopeDisposalFailure = exception;
                }
            }
        }
        catch (Exception exception)
        {
            executionFailure = Combine(executionFailure, exception);
            executionWasCancelled = false;
        }
        finally
        {
            linkedCancellation?.Dispose();
            registration?.Dispose();
        }

        if (scopeDisposalFailure is not null)
        {
            var failure = Combine(executionFailure, scopeDisposalFailure)!;
            logger.LogError(
                failure,
                "Background job {RunId} failed during execution or scope disposal.",
                job.RunId);
            return;
        }

        if (executionFailure is null)
        {
            logger.LogInformation("Background job {RunId} completed.", job.RunId);
            return;
        }

        if (executionWasCancelled)
        {
            logger.LogInformation("Background job {RunId} was cancelled.", job.RunId);
            return;
        }

        logger.LogError(executionFailure, "Background job {RunId} failed.", job.RunId);
    }

    private static Exception? Combine(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return new AggregateException(first, second);
    }

    private async Task RecoverAbandonedAsync(BackgroundJob job)
    {
        try
        {
            await recoveryService
                .RecoverAbandonedQueuedRunAsync(job, CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogWarning(
                "Background job {RunId} was abandoned during application shutdown.",
                job.RunId);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Background job {RunId} could not be reconciled during application shutdown.",
                job.RunId);
        }
    }
}
