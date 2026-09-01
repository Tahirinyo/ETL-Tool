using EtlTool.Application.Connections;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

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
    private readonly ISavedConnectionRevisionResolver? _connectionRevisionResolver;

    public RunAdmissionService(
        IPipelineService pipelineService,
        IPipelineReadinessService readinessService,
        PipelineSourceCommitCoordinator sourceCoordinator,
        IEtlRunRepository runRepository,
        IBackgroundJobQueue backgroundJobQueue,
        RunAdmissionOptions options,
        TimeProvider timeProvider,
        ISavedConnectionRevisionResolver? connectionRevisionResolver = null)
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
        _connectionRevisionResolver = connectionRevisionResolver;
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
                    await AdmitSourceAsync(snapshot).ConfigureAwait(false),
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

    private async Task<RunAdmissionResult> AdmitSourceAsync(
        PipelineRunSourceSnapshot snapshot)
    {
        var pipeline = snapshot.Pipeline
            ?? throw new InvalidOperationException("The ready run-source snapshot has no pipeline.");
        var source = snapshot.Source;

        if (pipeline.SourceType is SourceType.PostgreSql or SourceType.MongoDb)
        {
            var existingRuns = await _runRepository
                .ListByPipelineIdAsync(pipeline.Id, CancellationToken.None)
                .ConfigureAwait(false);
            if (existingRuns.Any(run => run.Status is EtlRunStatus.Queued or EtlRunStatus.Running))
            {
                return RunAdmissionResult.RunAlreadyActive(pipeline.Id, pipeline.Name);
            }
        }
        else if (source is null)
        {
            throw new InvalidOperationException("The ready run-source snapshot has no source.");
        }

        var (sourceConnection, destinationConnection) =
            await ResolveConnectionReferencesAsync(pipeline).ConfigureAwait(false);

        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = pipeline.Id,
            PipelineName = pipeline.Name,
            Status = EtlRunStatus.Queued,
            OriginalFileName = source?.OriginalFileName ?? string.Empty,
            StoredFilePath = source?.StoredFilePath ?? string.Empty,
            ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(
                pipeline,
                sourceConnection,
                destinationConnection)
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

    private async Task<(SavedConnectionReference? Source, SavedConnectionReference? Destination)>
        ResolveConnectionReferencesAsync(PipelineDefinition pipeline)
    {
        var sourceId = pipeline.SourceType switch
        {
            SourceType.PostgreSql => pipeline.PostgreSqlSource?.SavedConnectionId,
            SourceType.MongoDb => pipeline.MongoDbSource?.SavedConnectionId,
            _ => null
        };
        var sourceProvider = pipeline.SourceType switch
        {
            SourceType.PostgreSql => DatabaseProviderType.PostgreSql,
            SourceType.MongoDb => DatabaseProviderType.MongoDb,
            _ => DatabaseProviderType.Unspecified
        };
        var destinationId = pipeline.DestinationType switch
        {
            DestinationType.PostgreSql => pipeline.PostgreSqlDestination?.SavedConnectionId,
            DestinationType.MongoDb => pipeline.MongoDbDestinationConnectionId,
            _ => null
        };
        var destinationProvider = pipeline.DestinationType switch
        {
            DestinationType.PostgreSql => DatabaseProviderType.PostgreSql,
            DestinationType.MongoDb => DatabaseProviderType.MongoDb,
            _ => DatabaseProviderType.Unspecified
        };

        if (!sourceId.HasValue && !destinationId.HasValue)
        {
            return (null, null);
        }

        var resolver = _connectionRevisionResolver
            ?? throw new SavedConnectionResolutionException();
        var source = sourceId.HasValue
            ? await resolver.ResolveCurrentAsync(
                    sourceId.Value, sourceProvider, CancellationToken.None)
                .ConfigureAwait(false)
            : null;
        var destination = destinationId.HasValue
            ? await resolver.ResolveCurrentAsync(
                    destinationId.Value, destinationProvider, CancellationToken.None)
                .ConfigureAwait(false)
            : null;
        return (source, destination);
    }

    private static Exception Combine(Exception? primary, Exception additional) =>
        primary is null ? additional : new AggregateException(primary, additional);
}
