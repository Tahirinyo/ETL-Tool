using EtlTool.Application.Execution;
using EtlTool.Application.Loading;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Infrastructure.Execution;

public sealed class EtlRunBackgroundJobExecutor : IBackgroundJobExecutor
{
    private readonly IEtlRunRepository _runRepository;
    private readonly IPipelineDefinitionRepository _pipelineRepository;
    private readonly IBatchOrchestrator _orchestrator;
    private readonly IDataLoader _loader;
    private readonly TimeProvider _timeProvider;
    private readonly IRunSourceStreamFactory _sourceStreamFactory;

    public EtlRunBackgroundJobExecutor(
        IEtlRunRepository runRepository,
        IPipelineDefinitionRepository pipelineRepository,
        IBatchOrchestrator orchestrator,
        IDataLoader loader,
        TimeProvider timeProvider)
        : this(
            runRepository,
            pipelineRepository,
            orchestrator,
            loader,
            timeProvider,
            new RunSourceStreamFactory())
    {
    }

    internal EtlRunBackgroundJobExecutor(
        IEtlRunRepository runRepository,
        IPipelineDefinitionRepository pipelineRepository,
        IBatchOrchestrator orchestrator,
        IDataLoader loader,
        TimeProvider timeProvider,
        IRunSourceStreamFactory sourceStreamFactory)
    {
        ArgumentNullException.ThrowIfNull(runRepository);
        ArgumentNullException.ThrowIfNull(pipelineRepository);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(sourceStreamFactory);

        _runRepository = runRepository;
        _pipelineRepository = pipelineRepository;
        _orchestrator = orchestrator;
        _loader = loader;
        _timeProvider = timeProvider;
        _sourceStreamFactory = sourceStreamFactory;
    }

    public async Task ExecuteAsync(
        BackgroundJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        EtlRun? run = null;
        BatchExecutionProgress? latestProgress = null;
        var started = false;

        try
        {
            run = await _runRepository
                .GetByIdAsync(job.RunId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The queued ETL run no longer exists.");

            started = await _runRepository.TryStartAsync(
                run.Id,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                return;
            }

            var pipeline = await _pipelineRepository
                .GetByIdAsync(run.PipelineId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The ETL run pipeline no longer exists.");

            cancellationToken.ThrowIfCancellationRequested();
            await using var source = _sourceStreamFactory.Open(run);
            var target = new MongoTarget(
                pipeline.DestinationDatabase,
                pipeline.DestinationCollection);

            _ = await _orchestrator.ExecuteWithLoadResultAsync(
                source,
                pipeline,
                (batch, token) => _loader.UpsertBatchAsync(
                    batch,
                    target,
                    pipeline.UpsertKeyField,
                    token),
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

            if (latestProgress is null)
            {
                throw new InvalidOperationException(
                    "The batch orchestrator completed without a final progress snapshot.");
            }

            if (!await _runRepository.TryMarkTerminalAsync(
                run.Id,
                EtlRunStatus.Completed,
                _timeProvider.GetUtcNow(),
                latestProgress,
                systemError: null,
                cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The running ETL run rejected its completion update.");
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (exception is BatchExecutionCanceledException batchCancellation)
            {
                latestProgress = batchCancellation.ConfirmedProgress;
            }

            if (run is not null)
            {
                await MarkTerminalAfterFailureAsync(
                    run.Id,
                    EtlRunStatus.Interrupted,
                    latestProgress,
                    "ETL execution was interrupted.",
                    originalFailure: null).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception)
        {
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
                        exception).ConfigureAwait(false);
                }

                throw new OperationCanceledException(
                    "ETL execution was interrupted.",
                    exception,
                    cancellationToken);
            }

            if (started && run is not null)
            {
                var status = HasCommittedTargetWork(latestProgress)
                    ? EtlRunStatus.PartiallyCompleted
                    : EtlRunStatus.Failed;
                await MarkTerminalAfterFailureAsync(
                    run.Id,
                    status,
                    latestProgress,
                    SafeError(exception),
                    exception).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task MarkTerminalAfterFailureAsync(
        Guid runId,
        EtlRunStatus status,
        BatchExecutionProgress? progress,
        string systemError,
        Exception? originalFailure)
    {
        try
        {
            if (!await _runRepository.TryMarkTerminalAsync(
                runId,
                status,
                _timeProvider.GetUtcNow(),
                progress,
                systemError,
                CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The ETL run rejected its terminal status update.");
            }
        }
        catch (Exception terminalFailure) when (originalFailure is not null)
        {
            throw new AggregateException(originalFailure, terminalFailure);
        }
    }

    private static bool HasCommittedTargetWork(BatchExecutionProgress? progress) =>
        progress is not null
        && (progress.InsertedRows > 0 || progress.UpdatedRows > 0);

    private static string SafeError(Exception exception) => exception switch
    {
        BatchExecutionException => "MongoDB batch loading failed.",
        BatchLoadException => "MongoDB batch loading failed.",
        MongoTargetAccessException => "The MongoDB target is not accessible.",
        IOException => "The ETL source file could not be read.",
        UnauthorizedAccessException => "The ETL source file could not be accessed.",
        _ => "ETL execution failed."
    };
}
