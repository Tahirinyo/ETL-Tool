using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
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
    public async Task CommitRemapAsync_ActivationFailureRestoresAndPreservesPriorActiveSource()
    {
        var store = new RecordingSourceStore { ActivationSucceeds = false };
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var sourceReferenceId = Guid.NewGuid();
        var persistedState = "old";
        CancellationToken restoreToken = default;
        using var cancellation = new CancellationTokenSource();

        var status = await coordinator.CommitRemapAsync(
            pipelineId,
            sourceReferenceId,
            _ =>
            {
                persistedState = "new";
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            token =>
            {
                restoreToken = token;
                persistedState = "old";
                return Task.FromResult(true);
            },
            cancellation.Token);

        Assert.Equal(PipelineSourceCommitStatus.ActivationFailed, status);
        Assert.Equal("old", persistedState);
        Assert.Contains(sourceReferenceId, store.DiscardedSourceReferenceIds);
        Assert.Empty(store.RetiredPipelineIds);
        Assert.False(restoreToken.CanBeCanceled);
        Assert.All(store.DiscardTokens, token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task CommitRemapAsync_PersistenceRejectionDiscardsWithoutActivationOrRestoration()
    {
        var store = new RecordingSourceStore();
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var sourceReferenceId = Guid.NewGuid();
        var restoreCallCount = 0;

        var status = await coordinator.CommitRemapAsync(
            pipelineId,
            sourceReferenceId,
            _ => Task.FromResult(false),
            _ =>
            {
                restoreCallCount++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(PipelineSourceCommitStatus.PersistenceRejected, status);
        Assert.Equal(0, restoreCallCount);
        Assert.Empty(store.ActivatedSourceReferenceIds);
        Assert.Contains(sourceReferenceId, store.DiscardedSourceReferenceIds);
        Assert.Empty(store.RetiredPipelineIds);
        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await coordinator.CommitRemapAsync(
                pipelineId,
                Guid.NewGuid(),
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                CancellationToken.None));
    }

    [Fact]
    public async Task CommitRemapAsync_PersistenceExceptionCompensatesAndPreservesPrimaryFailure()
    {
        var store = new RecordingSourceStore();
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();
        var sourceReferenceId = Guid.NewGuid();
        var expected = new IOException("Persistence failed.");
        var restored = false;

        var actual = await Assert.ThrowsAsync<IOException>(() => coordinator.CommitRemapAsync(
            pipelineId,
            sourceReferenceId,
            _ => throw expected,
            _ =>
            {
                restored = true;
                return Task.FromResult(true);
            },
            CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.True(restored);
        Assert.Contains(sourceReferenceId, store.DiscardedSourceReferenceIds);
        Assert.Empty(store.ActivatedSourceReferenceIds);
        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await coordinator.CommitAsync(
                pipelineId,
                Guid.NewGuid(),
                _ => Task.FromResult(true),
                CancellationToken.None));
    }

    [Fact]
    public async Task CommitRemapAsync_SuccessActivatesExactSourceWithoutRestoration()
    {
        var store = new RecordingSourceStore();
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var sourceReferenceId = Guid.NewGuid();
        var restoreCallCount = 0;

        var status = await coordinator.CommitRemapAsync(
            Guid.NewGuid(),
            sourceReferenceId,
            _ => Task.FromResult(true),
            _ =>
            {
                restoreCallCount++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(PipelineSourceCommitStatus.Succeeded, status);
        Assert.Equal(sourceReferenceId, Assert.Single(store.ActivatedSourceReferenceIds));
        Assert.Equal(0, restoreCallCount);
        Assert.Empty(store.DiscardedSourceReferenceIds);
    }

    [Fact]
    public async Task CommitRemapAsync_ActivationExceptionPreservesCompensationFailures()
    {
        var activationFailure = new IOException("Activation failed.");
        var restorationFailure = new InvalidOperationException("Restoration failed.");
        var discardFailure = new UnauthorizedAccessException("Discard failed.");
        var store = new RecordingSourceStore
        {
            ActivationException = activationFailure,
            DiscardException = discardFailure
        };
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();

        var actual = await Assert.ThrowsAsync<IOException>(() => coordinator.CommitRemapAsync(
            pipelineId,
            Guid.NewGuid(),
            _ => Task.FromResult(true),
            _ => throw restorationFailure,
            CancellationToken.None));

        Assert.Same(activationFailure, actual);
        Assert.Same(restorationFailure, actual.Data["EtlTool.PipelineRestoreFailure"]);
        Assert.Same(discardFailure, actual.Data["EtlTool.SourceDiscardFailure"]);
        Assert.Empty(store.RetiredPipelineIds);
        store.ActivationException = null;
        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await coordinator.CommitRemapAsync(
                pipelineId,
                Guid.NewGuid(),
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                CancellationToken.None));
    }

    [Fact]
    public async Task CommitRemapAsync_FailedRestorationDoesNotReportSafeActivationFailure()
    {
        var store = new RecordingSourceStore { ActivationSucceeds = false };
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var pipelineId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitRemapAsync(
            pipelineId,
            Guid.NewGuid(),
            _ => Task.FromResult(true),
            _ => Task.FromResult(false),
            CancellationToken.None));

        Assert.Equal(
            PipelineSourceCommitStatus.PersistenceRejected,
            await coordinator.CommitRemapAsync(
                pipelineId,
                Guid.NewGuid(),
                _ => Task.FromResult(false),
                _ => Task.FromResult(true),
                CancellationToken.None));
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

    [Fact]
    public async Task CapturePreviewAsync_UsesPreviewSourceFactoryAndOwnsGenericSource()
    {
        var store = new RecordingSourceStore();
        var source = new TrackingLease();
        var factory = new RecordingPreviewSourceFactory(source);
        var coordinator = new PipelineSourceCommitCoordinator(store, factory);
        var pipeline = new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            SourceType = SourceType.PostgreSql,
            SourceOptions = new SourceOptions()
        };

        await using (var snapshot = await coordinator.CapturePreviewAsync(
                         pipeline.Id,
                         _ => Task.FromResult<PipelineDefinition?>(pipeline),
                         _ => new PipelineReadinessResult([]),
                         CancellationToken.None))
        {
            Assert.Equal(PipelinePreviewSnapshotStatus.Ready, snapshot.Status);
            Assert.Same(source, snapshot.Source);
        }

        Assert.Equal(1, factory.AcquireCallCount);
        Assert.Equal(0, store.AcquireCallCount);
        Assert.Equal(1, source.DisposeCallCount);
    }

    private sealed class RecordingSourceStore : IWizardSourceStore
    {
        public bool ActivationSucceeds { get; init; } = true;

        public Exception? ActivationException { get; set; }

        public Exception? DiscardException { get; init; }

        public List<Guid> ActivatedPipelineIds { get; } = [];

        public List<Guid> ActivatedSourceReferenceIds { get; } = [];

        public List<Guid> DiscardedSourceReferenceIds { get; } = [];

        public List<CancellationToken> DiscardTokens { get; } = [];

        public List<Guid> RetiredPipelineIds { get; } = [];

        public Func<CancellationToken, IWizardSourceLease?>? AcquireSource { get; init; }

        public int AcquireCallCount { get; private set; }

        public Task<bool> ActivateAsync(
            Guid pipelineId,
            Guid sourceReferenceId,
            CancellationToken cancellationToken)
        {
            ActivatedPipelineIds.Add(pipelineId);
            ActivatedSourceReferenceIds.Add(sourceReferenceId);
            if (ActivationException is not null)
            {
                throw ActivationException;
            }

            return Task.FromResult(ActivationSucceeds);
        }

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken)
        {
            DiscardedSourceReferenceIds.Add(sourceReferenceId);
            DiscardTokens.Add(cancellationToken);
            if (DiscardException is not null)
            {
                throw DiscardException;
            }

            return Task.CompletedTask;
        }

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            AcquireCallCount++;
            return Task.FromResult(AcquireSource?.Invoke(cancellationToken));
        }

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
        public int DisposeCallCount { get; private set; }

        public async IAsyncEnumerable<DataRow> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPreviewSourceFactory(IEtlSource source) : IPreviewSourceFactory
    {
        public int AcquireCallCount { get; private set; }

        public Task<IEtlSource?> AcquireAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            AcquireCallCount++;
            return Task.FromResult<IEtlSource?>(source);
        }
    }
}
