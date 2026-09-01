using System.Collections.Concurrent;
using EtlTool.Application.Extraction;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Sources;

public sealed class PipelineSourceCommitCoordinator
{
    private readonly IWizardSourceStore _sourceStore;
    private readonly IPreviewSourceFactory _previewSourceFactory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _pipelineGates = [];

    public PipelineSourceCommitCoordinator(
        IWizardSourceStore sourceStore,
        IPreviewSourceFactory? previewSourceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sourceStore);
        _sourceStore = sourceStore;
        _previewSourceFactory = previewSourceFactory ?? new WizardPreviewSourceFactory(sourceStore);
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

    public async Task<PipelineSourceCommitStatus> CommitRemapAsync(
        Guid pipelineId,
        Guid sourceReferenceId,
        Func<CancellationToken, Task<bool>> persistAsync,
        Func<CancellationToken, Task<bool>> restoreAsync,
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
        ArgumentNullException.ThrowIfNull(restoreAsync);

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
                await CompensateRemapPreservingFailureAsync(
                        sourceReferenceId,
                        restoreAsync,
                        exception)
                    .ConfigureAwait(false);
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
                await CompensateRemapPreservingFailureAsync(
                        sourceReferenceId,
                        restoreAsync,
                        exception)
                    .ConfigureAwait(false);
                throw;
            }

            await CompensateRemapAsync(sourceReferenceId, restoreAsync)
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
        IEtlSource? source = null;

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

            source = await _previewSourceFactory.AcquireAsync(
                    pipeline,
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

    public async Task<PipelineRunSourceSnapshot> CaptureRunSourceAsync(
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
        IWizardRunSourceReservation? source = null;
        var transferGateOwnership = false;

        try
        {
            var pipeline = await loadPipelineAsync(cancellationToken).ConfigureAwait(false);
            if (pipeline is null)
            {
                return PipelineRunSourceSnapshot.NotFound();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var readiness = evaluateReadiness(pipeline)
                ?? throw new InvalidOperationException("Pipeline readiness evaluation returned no result.");
            if (!readiness.IsReady)
            {
                return PipelineRunSourceSnapshot.NotReady(pipeline, readiness);
            }

            if (pipeline.SourceType is SourceType.Csv or SourceType.Xlsx)
            {
                source = await _sourceStore.ReserveForRunAsync(
                        pipeline.Id,
                        pipeline.SourceType,
                        pipeline.SourceOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (source is null)
                {
                    return PipelineRunSourceSnapshot.SourceUnavailable(pipeline, readiness);
                }
            }
            else if (pipeline.SourceType is not SourceType.PostgreSql and not SourceType.MongoDb)
            {
                throw new InvalidOperationException(
                    $"The pipeline source type '{pipeline.SourceType}' is not supported for execution.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = PipelineRunSourceSnapshot.Ready(
                pipeline,
                readiness,
                source,
                gate);
            source = null;
            transferGateOwnership = true;
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
                if (!transferGateOwnership)
                {
                    gate.Release();
                }
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

    private async Task CompensateRemapAsync(
        Guid sourceReferenceId,
        Func<CancellationToken, Task<bool>> restoreAsync)
    {
        List<Exception>? failures = null;

        try
        {
            if (!await restoreAsync(CancellationToken.None).ConfigureAwait(false))
            {
                failures =
                [
                    new InvalidOperationException(
                        "The previous pipeline configuration could not be restored.")
                ];
            }
        }
        catch (Exception exception)
        {
            failures = [exception];
        }

        try
        {
            await _sourceStore.DiscardAsync(sourceReferenceId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures ??= [];
            failures.Add(exception);
        }

        if (failures is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "The failed remap could not be compensated safely.",
                new AggregateException(failures));
        }
    }

    private async Task CompensateRemapPreservingFailureAsync(
        Guid sourceReferenceId,
        Func<CancellationToken, Task<bool>> restoreAsync,
        Exception primaryFailure)
    {
        try
        {
            if (!await restoreAsync(CancellationToken.None).ConfigureAwait(false))
            {
                primaryFailure.Data["EtlTool.PipelineRestoreFailure"] =
                    new InvalidOperationException(
                        "The previous pipeline configuration could not be restored.");
            }
        }
        catch (Exception restorationFailure)
        {
            primaryFailure.Data["EtlTool.PipelineRestoreFailure"] = restorationFailure;
        }

        try
        {
            await _sourceStore.DiscardAsync(sourceReferenceId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
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

    private sealed class WizardPreviewSourceFactory(IWizardSourceStore sourceStore) : IPreviewSourceFactory
    {
        public async Task<IEtlSource?> AcquireAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pipeline);

            return await sourceStore.AcquireAsync(
                    pipeline.Id,
                    pipeline.SourceType,
                    pipeline.SourceOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
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
    private IEtlSource? _source;

    private PipelinePreviewSnapshot(
        PipelinePreviewSnapshotStatus status,
        PipelineDefinition? pipeline,
        PipelineReadinessResult? readiness,
        IEtlSource? source)
    {
        Status = status;
        Pipeline = pipeline;
        Readiness = readiness;
        _source = source;
    }

    public PipelinePreviewSnapshotStatus Status { get; }

    public PipelineDefinition? Pipeline { get; }

    public PipelineReadinessResult? Readiness { get; }

    public IEtlSource? Source => _source;

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
        IEtlSource source) =>
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

public sealed class PipelineRunSourceSnapshot : IAsyncDisposable
{
    private IWizardRunSourceReservation? _source;
    private SemaphoreSlim? _gate;

    private PipelineRunSourceSnapshot(
        PipelineRunSourceSnapshotStatus status,
        PipelineDefinition? pipeline,
        PipelineReadinessResult? readiness,
        IWizardRunSourceReservation? source,
        SemaphoreSlim? gate)
    {
        Status = status;
        Pipeline = pipeline;
        Readiness = readiness;
        _source = source;
        _gate = gate;
    }

    public PipelineRunSourceSnapshotStatus Status { get; }

    public PipelineDefinition? Pipeline { get; }

    public PipelineReadinessResult? Readiness { get; }

    public IWizardRunSourceReservation? Source => _source;

    internal static PipelineRunSourceSnapshot NotFound() =>
        new(PipelineRunSourceSnapshotStatus.NotFound, null, null, null, null);

    internal static PipelineRunSourceSnapshot NotReady(
        PipelineDefinition pipeline,
        PipelineReadinessResult readiness) =>
        new(PipelineRunSourceSnapshotStatus.NotReady, pipeline, readiness, null, null);

    internal static PipelineRunSourceSnapshot SourceUnavailable(
        PipelineDefinition pipeline,
        PipelineReadinessResult readiness) =>
        new(PipelineRunSourceSnapshotStatus.SourceUnavailable, pipeline, readiness, null, null);

    internal static PipelineRunSourceSnapshot Ready(
        PipelineDefinition pipeline,
        PipelineReadinessResult readiness,
        IWizardRunSourceReservation? source,
        SemaphoreSlim gate) =>
        new(PipelineRunSourceSnapshotStatus.Ready, pipeline, readiness, source, gate);

    public void TransferSourceToRun()
    {
        if (_source is not null)
        {
            _source.TransferToRun();
            return;
        }

        if (Pipeline?.SourceType is not SourceType.PostgreSql and not SourceType.MongoDb)
        {
            throw new InvalidOperationException("The run source reservation is no longer available.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var source = Interlocked.Exchange(ref _source, null);
        var gate = Interlocked.Exchange(ref _gate, null);

        try
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate?.Release();
        }
    }
}

public enum PipelineRunSourceSnapshotStatus
{
    NotFound,
    NotReady,
    SourceUnavailable,
    Ready
}
