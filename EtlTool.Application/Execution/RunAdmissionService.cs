using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Execution;

public sealed class RunAdmissionService : IRunAdmissionService
{
    private const string AdmissionUnavailableMessage =
        "The run could not be admitted to background execution.";

    private readonly IPipelineService _pipelineService;
    private readonly IPipelineReadinessService _readinessService;
    private readonly PipelineSourceCommitCoordinator _sourceCoordinator;
    private readonly IEtlRunRepository _runRepository;
    private readonly IBackgroundJobQueue _backgroundJobQueue;
    private readonly RunAdmissionOptions _options;
    private readonly TimeProvider _timeProvider;

    public RunAdmissionService(
        IPipelineService pipelineService,
        IPipelineReadinessService readinessService,
        PipelineSourceCommitCoordinator sourceCoordinator,
        IEtlRunRepository runRepository,
        IBackgroundJobQueue backgroundJobQueue,
        RunAdmissionOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(pipelineService);
        ArgumentNullException.ThrowIfNull(readinessService);
        ArgumentNullException.ThrowIfNull(sourceCoordinator);
        ArgumentNullException.ThrowIfNull(runRepository);
        ArgumentNullException.ThrowIfNull(backgroundJobQueue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        options.Validate();

        _pipelineService = pipelineService;
        _readinessService = readinessService;
        _sourceCoordinator = sourceCoordinator;
        _runRepository = runRepository;
        _backgroundJobQueue = backgroundJobQueue;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<RunAdmissionResult> AdmitAsync(
        Guid pipelineId,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(pipelineId));
        }

        PipelineRunSourceSnapshot? snapshot = null;
        RunAdmissionResult result;

        try
        {
            snapshot = await _sourceCoordinator.CaptureRunSourceAsync(
                    pipelineId,
                    token => _pipelineService.GetByIdAsync(pipelineId, token),
                    _readinessService.Evaluate,
                    cancellationToken)
                .ConfigureAwait(false);

            result = snapshot.Status switch
            {
                PipelineRunSourceSnapshotStatus.NotFound =>
                    RunAdmissionResult.PipelineNotFound(),
                PipelineRunSourceSnapshotStatus.NotReady =>
                    RunAdmissionResult.PipelineNotReady(
                        snapshot.Pipeline!.Id,
                        snapshot.Pipeline.Name,
                        snapshot.Readiness!.Problems),
                PipelineRunSourceSnapshotStatus.SourceUnavailable =>
                    RunAdmissionResult.SourceUnavailable(
                        snapshot.Pipeline!.Id,
                        snapshot.Pipeline.Name),
                PipelineRunSourceSnapshotStatus.Ready =>
                    await AdmitReservedSourceAsync(snapshot).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The run-source snapshot status is invalid.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = RunAdmissionResult.Failed(pipelineId, string.Empty, exception);
        }

        if (snapshot is not null)
        {
            try
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                result = RunAdmissionResult.Failed(
                    result.PipelineId ?? pipelineId,
                    result.PipelineName,
                    Combine(result.Failure, cleanupFailure));
            }
        }

        return result;
    }

    private async Task<RunAdmissionResult> AdmitReservedSourceAsync(
        PipelineRunSourceSnapshot snapshot)
    {
        var pipeline = snapshot.Pipeline
            ?? throw new InvalidOperationException("The ready run-source snapshot has no pipeline.");
        var source = snapshot.Source
            ?? throw new InvalidOperationException("The ready run-source snapshot has no source.");
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = pipeline.Id,
            PipelineName = pipeline.Name,
            Status = EtlRunStatus.Queued,
            OriginalFileName = source.OriginalFileName,
            StoredFilePath = source.StoredFilePath
        };

        try
        {
            await _runRepository.AddAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return RunAdmissionResult.Failed(pipeline.Id, pipeline.Name, exception);
        }

        try
        {
            using var admissionCancellation = new CancellationTokenSource(
                _options.QueueAdmissionTimeout,
                _timeProvider);
            await _backgroundJobQueue
                .EnqueueAsync(new BackgroundJob(run.Id), admissionCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception enqueueFailure)
        {
            var failure = enqueueFailure;
            try
            {
                var interrupted = await _runRepository.TryMarkTerminalAsync(
                        run.Id,
                        EtlRunStatus.Interrupted,
                        _timeProvider.GetUtcNow(),
                        finalProgress: null,
                        AdmissionUnavailableMessage,
                        errorReportPath: null,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!interrupted)
                {
                    failure = Combine(
                        failure,
                        new InvalidOperationException(
                            "The queued ETL run rejected its admission-failure transition."));
                }
            }
            catch (Exception compensationFailure)
            {
                failure = Combine(failure, compensationFailure);
            }

            return RunAdmissionResult.Failed(pipeline.Id, pipeline.Name, failure);
        }

        snapshot.TransferSourceToRun();
        return RunAdmissionResult.Admitted(pipeline.Id, pipeline.Name, run.Id);
    }

    private static Exception Combine(Exception? primary, Exception additional) =>
        primary is null ? additional : new AggregateException(primary, additional);
}
