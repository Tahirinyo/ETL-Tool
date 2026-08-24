using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Sources;

public sealed class PipelineSourceCommitCoordinatorTests
{
    [Fact]
    public async Task CommitAsync_DifferentPipelinesDoNotShareACommitGate()
    {
        var store = new RecordingSourceStore();
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var firstPipelineId = Guid.NewGuid();
        var secondPipelineId = Guid.NewGuid();
        var firstPersisted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstCommit = coordinator.CommitAsync(
            firstPipelineId,
            Guid.NewGuid(),
            async cancellationToken =>
            {
                firstPersisted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);
        await firstPersisted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondCommit = coordinator.CommitAsync(
            secondPipelineId,
            Guid.NewGuid(),
            _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await secondCommit.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(secondPipelineId, store.ActivatedPipelineIds);

        releaseFirst.TrySetResult();
        Assert.Equal(PipelineSourceCommitStatus.Succeeded, await firstCommit);
    }

    [Fact]
    public async Task CommitAsync_PersistenceCancellationDiscardsPendingAndReleasesGate()
    {
        var store = new RecordingSourceStore();
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var cancelledReferenceId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.CommitAsync(
                pipelineId,
                cancelledReferenceId,
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                cancellation.Token));

        Assert.Contains(cancelledReferenceId, store.DiscardedSourceReferenceIds);
        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await coordinator.CommitAsync(
                pipelineId,
                Guid.NewGuid(),
                _ => Task.FromResult(true),
                CancellationToken.None));
    }

    [Fact]
    public async Task CommitAsync_ActivationFailureInvalidatesThePriorActiveSource()
    {
        var store = new RecordingSourceStore { ActivationSucceeds = false };
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var sourceReferenceId = Guid.NewGuid();

        var status = await coordinator.CommitAsync(
            pipelineId,
            sourceReferenceId,
            _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(PipelineSourceCommitStatus.ActivationFailed, status);
        Assert.Contains(sourceReferenceId, store.DiscardedSourceReferenceIds);
        Assert.Contains(pipelineId, store.RetiredPipelineIds);
    }

    [Fact]
    public async Task CommitAsync_CancellationWhileQueuedLeavesOwnerAndGateUsable()
    {
        var store = new RecordingSourceStore();
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var ownerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerReferenceId = Guid.NewGuid();
        var cancelledReferenceId = Guid.NewGuid();

        var owner = coordinator.CommitAsync(
            pipelineId,
            ownerReferenceId,
            async cancellationToken =>
            {
                ownerEntered.TrySetResult();
                await releaseOwner.Task.WaitAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);
        await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var queued = coordinator.CommitAsync(
            pipelineId,
            cancelledReferenceId,
            _ => Task.FromResult(true),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.DoesNotContain(cancelledReferenceId, store.DiscardedSourceReferenceIds);

        releaseOwner.TrySetResult();
        Assert.Equal(PipelineSourceCommitStatus.Succeeded, await owner);
        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await coordinator.CommitAsync(
                pipelineId,
                Guid.NewGuid(),
                _ => Task.FromResult(true),
                CancellationToken.None));
    }

    [Fact]
    public async Task CommitAsync_CancellationAfterPersistenceStillActivatesExactSource()
    {
        var store = new RecordingSourceStore();
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var sourceReferenceId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var status = await coordinator.CommitAsync(
            pipelineId,
            sourceReferenceId,
            _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            cancellation.Token);

        Assert.Equal(PipelineSourceCommitStatus.Succeeded, status);
        Assert.Contains(sourceReferenceId, store.ActivatedSourceReferenceIds);
        Assert.DoesNotContain(sourceReferenceId, store.DiscardedSourceReferenceIds);
    }

    [Fact]
    public async Task CapturePreviewAsync_CancellationAfterAcquisitionDisposesLeaseAndReleasesGate()
    {
        using var cancellation = new CancellationTokenSource();
        var lease = new TrackingLease();
        var store = new RecordingSourceStore
        {
            AcquireSource = _ =>
            {
                cancellation.Cancel();
                return lease;
            }
        };
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = pipelineId,
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions()
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.CapturePreviewAsync(
                pipelineId,
                _ => Task.FromResult<PipelineDefinition?>(pipeline),
                _ => new PipelineReadinessResult([]),
                cancellation.Token));

        Assert.Equal(1, lease.DisposeCallCount);
        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await coordinator.CommitAsync(
                pipelineId,
                Guid.NewGuid(),
                _ => Task.FromResult(true),
                CancellationToken.None));
    }

    private sealed class RecordingSourceStore : IWizardSourceStore
    {
        public bool ActivationSucceeds { get; init; } = true;

        public List<Guid> ActivatedPipelineIds { get; } = [];

        public List<Guid> ActivatedSourceReferenceIds { get; } = [];

        public List<Guid> DiscardedSourceReferenceIds { get; } = [];

        public List<Guid> RetiredPipelineIds { get; } = [];

        public Func<CancellationToken, IWizardSourceLease?>? AcquireSource { get; init; }

        public Task<bool> ActivateAsync(
            Guid pipelineId,
            Guid sourceReferenceId,
            CancellationToken cancellationToken)
        {
            ActivatedPipelineIds.Add(pipelineId);
            ActivatedSourceReferenceIds.Add(sourceReferenceId);
            return Task.FromResult(ActivationSucceeds);
        }

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken)
        {
            DiscardedSourceReferenceIds.Add(sourceReferenceId);
            return Task.CompletedTask;
        }

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) =>
            Task.FromResult(AcquireSource?.Invoke(cancellationToken));

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            RetiredPipelineIds.Add(pipelineId);
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingLease : IWizardSourceLease
    {
        public Stream Content { get; } = new MemoryStream();

        public int DisposeCallCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            await Content.DisposeAsync();
        }
    }
}
