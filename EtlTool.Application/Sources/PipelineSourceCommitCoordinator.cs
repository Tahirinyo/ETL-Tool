using System.Collections.Concurrent;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Sources;

public sealed class PipelineSourceCommitCoordinator
{
    private readonly IWizardSourceStore _sourceStore;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _pipelineGates = [];

    public PipelineSourceCommitCoordinator(IWizardSourceStore sourceStore)
    {
        ArgumentNullException.ThrowIfNull(sourceStore);
        _sourceStore = sourceStore;
    }

    public async Task<PipelineSourceCommitStatus> CommitAsync(
        Guid pipelineId,
        Guid sourceReferenceId,
        Func<CancellationToken, Task<bool>> persistAsync,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(pipelineId));
        }

        if (sourceReferenceId == Guid.Empty)
        {
            throw new ArgumentException("Source reference identifier cannot be empty.", nameof(sourceReferenceId));
        }

        ArgumentNullException.ThrowIfNull(persistAsync);

        var gate = GetGate(pipelineId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            bool persisted;
            try
            {
                persisted = await persistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await DiscardPreservingFailureAsync(sourceReferenceId, exception).ConfigureAwait(false);
                throw;
            }

            if (!persisted)
            {
                await _sourceStore.DiscardAsync(sourceReferenceId, CancellationToken.None)
                    .ConfigureAwait(false);
                return PipelineSourceCommitStatus.PersistenceRejected;
            }

            try
            {
                if (await _sourceStore.ActivateAsync(
                        pipelineId,
                        sourceReferenceId,
                        CancellationToken.None)
                    .ConfigureAwait(false))
                {
                    return PipelineSourceCommitStatus.Succeeded;
                }
            }
            catch (Exception exception)
            {
                await CleanUpActivationFailureAsync(
                        pipelineId,
                        sourceReferenceId,
                        exception)
                    .ConfigureAwait(false);
                throw;
            }

            await CleanUpActivationFailureAsync(
                    pipelineId,
                    sourceReferenceId,
                    primaryFailure: null)
                .ConfigureAwait(false);
            return PipelineSourceCommitStatus.ActivationFailed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PipelinePreviewSnapshot> CapturePreviewAsync(
        Guid pipelineId,
        Func<CancellationToken, Task<PipelineDefinition?>> loadPipelineAsync,
        Func<PipelineDefinition, PipelineReadinessResult> evaluateReadiness,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(pipelineId));
        }

        ArgumentNullException.ThrowIfNull(loadPipelineAsync);
        ArgumentNullException.ThrowIfNull(evaluateReadiness);

        var gate = GetGate(pipelineId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IWizardSourceLease? source = null;

        try
        {
            var pipeline = await loadPipelineAsync(cancellationToken).ConfigureAwait(false);
            if (pipeline is null)
            {
                return PipelinePreviewSnapshot.NotFound();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var readiness = evaluateReadiness(pipeline)
                ?? throw new InvalidOperationException("Pipeline readiness evaluation returned no result.");
            if (!readiness.IsReady)
            {
                return PipelinePreviewSnapshot.NotReady(pipeline, readiness);
            }

            source = await _sourceStore.AcquireAsync(
                    pipeline.Id,
                    pipeline.SourceType,
                    pipeline.SourceOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                return PipelinePreviewSnapshot.SourceUnavailable(pipeline, readiness);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = PipelinePreviewSnapshot.Ready(pipeline, readiness, source);
            source = null;
            return snapshot;
        }
        finally
        {
            try
            {
                if (source is not null)
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private SemaphoreSlim GetGate(Guid pipelineId) =>
        _pipelineGates.GetOrAdd(pipelineId, static _ => new SemaphoreSlim(1, 1));

    private async Task DiscardPreservingFailureAsync(
        Guid sourceReferenceId,
        Exception primaryFailure)
    {
        try
        {
            await _sourceStore.DiscardAsync(sourceReferenceId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
            when (cleanupFailure is IOException or UnauthorizedAccessException)
        {
            primaryFailure.Data["EtlTool.SourceDiscardFailure"] = cleanupFailure;
        }
    }

    private async Task CleanUpActivationFailureAsync(
        Guid pipelineId,
        Guid sourceReferenceId,
        Exception? primaryFailure)
    {
        List<Exception>? cleanupFailures = null;

        try
        {
            await _sourceStore.DiscardAsync(sourceReferenceId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupFailures = [exception];
        }

        try
        {
            await _sourceStore.RetireActiveAsync(pipelineId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupFailures ??= [];
            cleanupFailures.Add(exception);
        }

        if (cleanupFailures is not { Count: > 0 })
        {
            return;
        }

        var cleanupFailure = new AggregateException(cleanupFailures);
        if (primaryFailure is not null)
        {
            primaryFailure.Data["EtlTool.SourceActivationCleanupFailure"] = cleanupFailure;
            return;
        }

        throw new IOException(
            "The unavailable source could not be cleaned up completely.",
            cleanupFailure);
    }
}

public enum PipelineSourceCommitStatus
{
    Succeeded,
    PersistenceRejected,
    ActivationFailed
}

public sealed class PipelinePreviewSnapshot : IAsyncDisposable
{
    private IWizardSourceLease? _source;

    private PipelinePreviewSnapshot(
        PipelinePreviewSnapshotStatus status,
        PipelineDefinition? pipeline,
        PipelineReadinessResult? readiness,
        IWizardSourceLease? source)
    {
        Status = status;
        Pipeline = pipeline;
        Readiness = readiness;
        _source = source;
    }

    public PipelinePreviewSnapshotStatus Status { get; }

    public PipelineDefinition? Pipeline { get; }

    public PipelineReadinessResult? Readiness { get; }

    public IWizardSourceLease? Source => _source;

    internal static PipelinePreviewSnapshot NotFound() =>
        new(PipelinePreviewSnapshotStatus.NotFound, null, null, null);

    internal static PipelinePreviewSnapshot NotReady(
        PipelineDefinition pipeline,
        PipelineReadinessResult readiness) =>
        new(PipelinePreviewSnapshotStatus.NotReady, pipeline, readiness, null);

    internal static PipelinePreviewSnapshot SourceUnavailable(
        PipelineDefinition pipeline,
        PipelineReadinessResult readiness) =>
        new(PipelinePreviewSnapshotStatus.SourceUnavailable, pipeline, readiness, null);

    internal static PipelinePreviewSnapshot Ready(
        PipelineDefinition pipeline,
        PipelineReadinessResult readiness,
        IWizardSourceLease source) =>
        new(PipelinePreviewSnapshotStatus.Ready, pipeline, readiness, source);

    public async ValueTask DisposeAsync()
    {
        var source = Interlocked.Exchange(ref _source, null);
        if (source is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public enum PipelinePreviewSnapshotStatus
{
    NotFound,
    NotReady,
    SourceUnavailable,
    Ready
}
