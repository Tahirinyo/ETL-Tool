using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Reporting;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.Reporting;
using Microsoft.Extensions.Logging;

namespace EtlTool.Infrastructure.Execution;

public sealed class EtlRunBackgroundJobExecutor : IBackgroundJobExecutor
{
    private const string MissingExecutionConfigurationMessage =
        "The admitted ETL execution configuration is unavailable.";

    private readonly IEtlRunRepository _runRepository;
    private readonly IBatchOrchestrator _orchestrator;
    private readonly IDataLoaderResolver _loaderResolver;
    private readonly TimeProvider _timeProvider;
    private readonly IRunSourceStore _sourceStore;
    private readonly IErrorReportWriter _errorReportWriter;
    private readonly IErrorReportStore _errorReportStore;
    private readonly ILogger<EtlRunBackgroundJobExecutor> _logger;

    public EtlRunBackgroundJobExecutor(
        IEtlRunRepository runRepository,
        IBatchOrchestrator orchestrator,
        IDataLoaderResolver loaderResolver,
        TimeProvider timeProvider,
        IErrorReportWriter errorReportWriter,
        IRunSourceStore sourceStore,
        IErrorReportStore errorReportStore,
        ILogger<EtlRunBackgroundJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(runRepository);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(loaderResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(sourceStore);
        ArgumentNullException.ThrowIfNull(errorReportWriter);
        ArgumentNullException.ThrowIfNull(errorReportStore);
        ArgumentNullException.ThrowIfNull(logger);

        _runRepository = runRepository;
        _orchestrator = orchestrator;
        _loaderResolver = loaderResolver;
        _timeProvider = timeProvider;
        _sourceStore = sourceStore;
        _errorReportWriter = errorReportWriter;
        _errorReportStore = errorReportStore;
        _logger = logger;
    }

    internal EtlRunBackgroundJobExecutor(
        IEtlRunRepository runRepository,
        IBatchOrchestrator orchestrator,
        IDataLoaderResolver loaderResolver,
        TimeProvider timeProvider,
        IRunSourceStore sourceStore,
        IErrorReportWriter errorReportWriter,
        IErrorReportStore errorReportStore,
        ILogger<EtlRunBackgroundJobExecutor> logger)
        : this(runRepository, orchestrator, loaderResolver, timeProvider,
            errorReportWriter, sourceStore, errorReportStore, logger)
    {
    }

    public async Task ExecuteAsync(
        BackgroundJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        EtlRun? run = null;
        BatchExecutionProgress? latestProgress = null;
        var started = false;
        var ownsSourceCleanup = false;
        var legacyRecoveryAttempted = false;
        IEtlSource? source = null;
        InvalidRowReportSession? errorReportSession = null;
        var reportFinalizationAttempted = false;
        var executionFailed = false;

        try
        {
            run = await _runRepository
                .GetByIdAsync(job.RunId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The queued ETL run no longer exists.");
            var observedStatus = run.Status;

            started = await _runRepository.TryStartAsync(
                run.Id,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            ownsSourceCleanup = started;
            if (!started)
            {
                if (observedStatus != EtlRunStatus.Running
                    || run.ExecutionConfiguration is not null)
                {
                    return;
                }

                legacyRecoveryAttempted = true;
                ownsSourceCleanup = await _runRepository
                    .TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
                        run.Id,
                        _timeProvider.GetUtcNow(),
                        MissingExecutionConfigurationMessage,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!ownsSourceCleanup)
                {
                    return;
                }

                throw new InvalidOperationException(MissingExecutionConfigurationMessage);
            }

            var pipeline = run.ExecutionConfiguration?.ToPipelineDefinition()
                ?? throw new InvalidOperationException(MissingExecutionConfigurationMessage);

            cancellationToken.ThrowIfCancellationRequested();
            var loader = _loaderResolver.Resolve(pipeline.DestinationType);
            source = await _sourceStore
                .OpenAsync(run, cancellationToken)
                .ConfigureAwait(false);
            errorReportSession = new InvalidRowReportSession(
                _errorReportWriter,
                _errorReportStore,
                run,
                pipeline.ExpectedSchema.Select(field => field.Name).ToArray(),
                pipeline.UpsertKeyField,
                cancellationToken);

            _ = await _orchestrator.ExecuteWithLoadResultAsync(
                source,
                pipeline,
                loader,
                errorReportSession.ReportAsync,
                async (progress, token) =>
                {
                    latestProgress = progress;
                    if (!await _runRepository
                        .TryUpdateProgressAsync(run.Id, progress, token)
                        .ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "The running ETL run rejected its progress update.");
                    }
                },
                cancellationToken).ConfigureAwait(false);
            reportFinalizationAttempted = true;
            var reportReference = await errorReportSession
                .CompleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (latestProgress is null)
            {
                throw new InvalidOperationException(
                    "The batch orchestrator completed without a final progress snapshot.");
            }

            await MarkTerminalAsync(
                run.Id,
                EtlRunStatus.Completed,
                latestProgress,
                systemError: null,
                reportReference,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            executionFailed = true;
            if (exception is BatchExecutionCanceledException batchCancellation)
            {
                latestProgress = batchCancellation.ConfirmedProgress;
            }

            if (!legacyRecoveryAttempted && run is not null)
            {
                await MarkTerminalAfterFailureAsync(
                    run.Id,
                    EtlRunStatus.Interrupted,
                    latestProgress,
                    "ETL execution was interrupted.",
                    errorReportPath: null,
                    originalFailure: null).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception)
        {
            executionFailed = true;
            if (legacyRecoveryAttempted)
            {
                throw;
            }

            if (exception is BatchExecutionException batchFailure)
            {
                latestProgress = batchFailure.ConfirmedProgress;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (run is not null)
                {
                    await MarkTerminalAfterFailureAsync(
                        run.Id,
                        EtlRunStatus.Interrupted,
                        latestProgress,
                        "ETL execution was interrupted.",
                        errorReportPath: null,
                        originalFailure: exception).ConfigureAwait(false);
                }

                throw new OperationCanceledException(
                    "ETL execution was interrupted.",
                    exception,
                    cancellationToken);
            }

            if (started && run is not null)
            {
                var failure = exception;
                string? reportReference = null;
                if (!reportFinalizationAttempted && errorReportSession?.HasInvalidRows == true)
                {
                    reportFinalizationAttempted = true;
                    try
                    {
                        reportReference = await errorReportSession
                            .CompleteAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception reportException)
                    {
                        failure = reportException;
                    }
                }

                var status = HasCommittedTargetWork(latestProgress)
                    ? EtlRunStatus.PartiallyCompleted
                    : EtlRunStatus.Failed;
                await MarkTerminalAfterFailureAsync(
                    run.Id,
                    status,
                    latestProgress,
                    SafeError(failure),
                    reportReference,
                    failure).ConfigureAwait(false);

                if (!ReferenceEquals(failure, exception))
                {
                    throw failure;
                }
            }

            throw;
        }
        finally
        {
            if (errorReportSession is not null)
            {
                try
                {
                    await errorReportSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (executionFailed)
                {
                    _logger.LogDebug(cleanupException,
                        "Error-report cleanup for ETL run {RunId} failed after execution had already ended.",
                        run?.Id);
                }
            }

            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            if (ownsSourceCleanup && run is not null)
            {
                try
                {
                    await _sourceStore.ReleaseAsync(run, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (cleanupException is IOException
                    or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    _logger.LogWarning(cleanupException,
                        "Temporary source for ETL run {RunId} could not be removed and will be retried by orphan cleanup.",
                        run.Id);
                }
            }
        }
    }

    private async Task MarkTerminalAfterFailureAsync(
        Guid runId,
        EtlRunStatus status,
        BatchExecutionProgress? progress,
        string systemError,
        string? errorReportPath,
        Exception? originalFailure)
    {
        try
        {
            await MarkTerminalAsync(
                runId,
                status,
                progress,
                systemError,
                errorReportPath,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception terminalFailure) when (originalFailure is not null)
        {
            throw new AggregateException(originalFailure, terminalFailure);
        }
    }

    private async Task MarkTerminalAsync(
        Guid runId,
        EtlRunStatus status,
        BatchExecutionProgress? progress,
        string? systemError,
        string? errorReportPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _runRepository.TryMarkTerminalAsync(
                runId,
                status,
                _timeProvider.GetUtcNow(),
                progress,
                systemError,
                errorReportPath,
                cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The ETL run rejected its terminal status update.");
            }
        }
        catch
        {
            if (errorReportPath is not null)
            {
                try
                {
                    await _errorReportStore.DeletePublishedAsync(
                        new EtlRun { Id = runId, ErrorReportPath = errorReportPath },
                        errorReportPath,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(cleanupException,
                        "Published error report for ETL run {RunId} could not be removed after terminal persistence failed.",
                        runId);
                }
            }

            throw;
        }
    }

    private static bool HasCommittedTargetWork(BatchExecutionProgress? progress) =>
        progress is not null
        && (progress.InsertedRows > 0 || progress.UpdatedRows > 0);

    private static string SafeError(Exception exception) => exception switch
    {
        BatchExecutionException => "Destination batch loading failed.",
        BatchLoadException => "Destination batch loading failed.",
        MongoTargetAccessException => "The MongoDB target is not accessible.",
        MongoSourceAccessException => "The configured MongoDB source could not be accessed.",
        MongoSourceMetadataObjectNotFoundException => exception.Message,
        MongoSourceSchemaChangedException => MongoSourceSchemaChangedException.SafeMessage,
        PostgreSqlSourceSchemaChangedException => PostgreSqlSourceSchemaChangedException.SafeMessage,
        PostgreSqlDestinationPreparationException => PostgreSqlDestinationPreparationException.SafeMessage,
        IOException => "The ETL source file could not be read.",
        UnauthorizedAccessException => "The ETL source file could not be accessed.",
        ErrorReportGenerationException => "Error report generation failed.",
        InvalidOperationException when string.Equals(
            exception.Message,
            MissingExecutionConfigurationMessage,
            StringComparison.Ordinal) => MissingExecutionConfigurationMessage,
        _ => "ETL execution failed."
    };
}
