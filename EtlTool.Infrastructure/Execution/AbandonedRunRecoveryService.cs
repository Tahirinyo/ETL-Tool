using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace EtlTool.Infrastructure.Execution;

public sealed class AbandonedRunRecoveryService(
    IEtlRunRepository runRepository,
    IRunSourceFileStore sourceFileStore,
    TimeProvider timeProvider,
    ILogger<AbandonedRunRecoveryService> logger)
{
    private const string StartupDiagnostic =
        "ETL execution was interrupted because the application restarted before the run completed.";
    private const string ShutdownDiagnostic =
        "ETL execution was interrupted because the application stopped before the queued run started.";
    private const string MissingExecutionConfigurationDiagnostic =
        "The admitted ETL execution configuration is unavailable.";

    public async Task RecoverStaleRunsAsync(CancellationToken cancellationToken)
    {
        var staleRuns = await runRepository
            .ListNonTerminalAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var run in staleRuns)
        {
            if (run.Status == EtlRunStatus.Running
                && run.ExecutionConfiguration is null)
            {
                await FailLegacyRunAndCleanupAsync(run, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await InterruptAndCleanupAsync(
                run,
                run.Status,
                StartupDiagnostic,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RecoverAbandonedQueuedRunAsync(
        BackgroundJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var run = await runRepository
            .GetByIdAsync(job.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return;
        }

        await InterruptAndCleanupAsync(
            run,
            EtlRunStatus.Queued,
            ShutdownDiagnostic,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task InterruptAndCleanupAsync(
        EtlRun run,
        EtlRunStatus expectedStatus,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        if (!await runRepository.TryInterruptAsync(
            run.Id,
            expectedStatus,
            timeProvider.GetUtcNow(),
            run.ProcessedRows,
            diagnostic,
            cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await DeleteSourceAsync(run).ConfigureAwait(false);
    }

    private async Task FailLegacyRunAndCleanupAsync(
        EtlRun run,
        CancellationToken cancellationToken)
    {
        if (!await runRepository.TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
            run.Id,
            timeProvider.GetUtcNow(),
            MissingExecutionConfigurationDiagnostic,
            cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await DeleteSourceAsync(run).ConfigureAwait(false);
    }

    private async Task DeleteSourceAsync(EtlRun run)
    {
        try
        {
            await sourceFileStore.DeleteAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(
                exception,
                "Temporary source for recovered ETL run {RunId} could not be removed and will be retried by orphan cleanup.",
                run.Id);
        }
    }
}
