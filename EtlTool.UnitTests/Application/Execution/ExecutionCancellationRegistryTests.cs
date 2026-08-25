using EtlTool.Application.Execution;

namespace EtlTool.UnitTests.Application.Execution;

public sealed class ExecutionCancellationRegistryTests
{
    [Fact]
    public void Register_ExposesTokenAndRejectsDuplicateLiveRegistration()
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();
        using var registration = registry.Register(runId);

        Assert.True(registration.Token.CanBeCanceled);
        Assert.False(registration.Token.IsCancellationRequested);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(runId));

        Assert.Contains(runId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.True(registry.TryRequestCancellation(runId));
        Assert.True(registration.Token.IsCancellationRequested);
    }

    [Fact]
    public void TryRequestCancellation_CancelsOnlyRequestedRunAndRemainsIdempotent()
    {
        var registry = new ExecutionCancellationRegistry();
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        using var first = registry.Register(firstRunId);
        using var second = registry.Register(secondRunId);

        Assert.True(registry.TryRequestCancellation(firstRunId));
        Assert.True(registry.TryRequestCancellation(firstRunId));

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.True(registry.TryRequestCancellation(secondRunId));
    }

    [Fact]
    public void Dispose_RemovesOnlyOwningRegistrationAndIsIdempotent()
    {
        var registry = new ExecutionCancellationRegistry();
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var first = registry.Register(firstRunId);
        using var second = registry.Register(secondRunId);

        first.Dispose();
        first.Dispose();

        Assert.False(registry.TryRequestCancellation(firstRunId));
        Assert.True(registry.TryRequestCancellation(secondRunId));
        Assert.True(second.Token.IsCancellationRequested);
    }

    [Fact]
    public void Register_AllowsReuseAfterCleanupAndStaleLeaseCannotAffectReplacement()
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();
        var staleRegistration = registry.Register(runId);
        var staleToken = staleRegistration.Token;
        IExecutionCancellationRegistration? replacement = null;
        using var callback = staleToken.Register(() =>
        {
            staleRegistration.Dispose();
            replacement = registry.Register(runId);
        });

        Assert.True(registry.TryRequestCancellation(runId));
        Assert.NotNull(replacement);
        staleRegistration.Dispose();

        Assert.False(replacement.Token.IsCancellationRequested);
        Assert.True(registry.TryRequestCancellation(runId));
        Assert.True(replacement.Token.IsCancellationRequested);
        Assert.True(staleToken.IsCancellationRequested);

        replacement.Dispose();
        Assert.False(registry.TryRequestCancellation(runId));
    }

    [Fact]
    public void TryRequestCancellation_ReturnsFalseForUnknownAndCleanedUpRuns()
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();

        Assert.False(registry.TryRequestCancellation(runId));

        var registration = registry.Register(runId);
        registration.Dispose();

        Assert.False(registry.TryRequestCancellation(runId));
    }

    [Theory]
    [InlineData(TerminalOutcome.Success)]
    [InlineData(TerminalOutcome.Failure)]
    [InlineData(TerminalOutcome.Cancellation)]
    public async Task RegistrationCleanup_CoversEverySimulatedTerminalPath(
        TerminalOutcome outcome)
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();

        switch (outcome)
        {
            case TerminalOutcome.Success:
                await SimulateExecutionAsync(registry, runId, static _ => Task.CompletedTask);
                break;
            case TerminalOutcome.Failure:
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    SimulateExecutionAsync(
                        registry,
                        runId,
                        static _ => throw new InvalidOperationException("Simulated execution failure.")));
                break;
            case TerminalOutcome.Cancellation:
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    SimulateExecutionAsync(
                        registry,
                        runId,
                        token =>
                        {
                            Assert.True(registry.TryRequestCancellation(runId));
                            token.ThrowIfCancellationRequested();
                            return Task.CompletedTask;
                        }));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Assert.False(registry.TryRequestCancellation(runId));
    }

    [Fact]
    public async Task CancellationAndDisposal_AreCoordinatedForBothOrderings()
    {
        var registry = new ExecutionCancellationRegistry();
        var cancelFirstRunId = Guid.NewGuid();
        var unaffectedRunId = Guid.NewGuid();
        var cancelFirst = registry.Register(cancelFirstRunId);
        using var unaffected = registry.Register(unaffectedRunId);
        var callbackEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = cancelFirst.Token.Register(() =>
        {
            callbackEntered.SetResult(true);
            releaseCallback.Task.GetAwaiter().GetResult();
        });

        var cancellation = Task.Run(() => registry.TryRequestCancellation(cancelFirstRunId));
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = Task.Run(cancelFirst.Dispose);
        releaseCallback.SetResult(true);

        Assert.True(await cancellation);
        await disposal;
        Assert.False(registry.TryRequestCancellation(cancelFirstRunId));
        Assert.False(unaffected.Token.IsCancellationRequested);

        var disposeFirstRunId = Guid.NewGuid();
        var disposeFirst = registry.Register(disposeFirstRunId);
        var allowCancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCancellation = Task.Run(async () =>
        {
            await allowCancellation.Task;
            await disposalCompleted.Task;
            return registry.TryRequestCancellation(disposeFirstRunId);
        });
        var earlyDisposal = Task.Run(async () =>
        {
            await allowCancellation.Task;
            disposeFirst.Dispose();
            disposalCompleted.SetResult(true);
        });

        allowCancellation.SetResult(true);

        await earlyDisposal;
        Assert.False(await lateCancellation);
        Assert.False(registry.TryRequestCancellation(disposeFirstRunId));
        Assert.True(registry.TryRequestCancellation(unaffectedRunId));
    }

    [Fact]
    public async Task CancellationCallback_CanWaitForCrossThreadDisposalWithoutDeadlock()
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();
        var registration = registry.Register(runId);
        var disposalStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposal = null;
        var disposalCompletedInsideCallback = false;
        using var callback = registration.Token.Register(() =>
        {
            disposal = Task.Run(() =>
            {
                disposalStarted.SetResult(true);
                registration.Dispose();
            });
            disposalStarted.Task.GetAwaiter().GetResult();
            disposalCompletedInsideCallback = disposal.Wait(TimeSpan.FromSeconds(5));
        });

        var cancellation = Task.Run(() => registry.TryRequestCancellation(runId));

        Assert.True(await cancellation.WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.NotNull(disposal);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposalCompletedInsideCallback);
        Assert.False(registry.TryRequestCancellation(runId));
    }

    [Fact]
    public void CancellationCallbackException_PropagatesAndCompletesDeferredDisposal()
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();
        var registration = registry.Register(runId);
        var callbackFailure = new InvalidOperationException("Cancellation callback failed.");
        using var callback = registration.Token.Register(() =>
        {
            registration.Dispose();
            throw callbackFailure;
        });

        var exception = Assert.Throws<AggregateException>(() =>
            registry.TryRequestCancellation(runId));

        Assert.Contains(callbackFailure, exception.InnerExceptions);
        Assert.False(registry.TryRequestCancellation(runId));
        registration.Dispose();
    }

    [Fact]
    public async Task ConcurrentRepeatedCancellation_ReturnsOnlyAfterTokenIsSignalled()
    {
        var firstCancellationReserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var signallingOperations = 0;
        var registry = new ExecutionCancellationRegistry(() =>
        {
            if (Interlocked.Increment(ref signallingOperations) == 1)
            {
                firstCancellationReserved.SetResult(true);
                releaseFirstCancellation.Task.GetAwaiter().GetResult();
            }
        });
        var runId = Guid.NewGuid();
        var registration = registry.Register(runId);
        var firstCancellation = Task.Run(() => registry.TryRequestCancellation(runId));

        await firstCancellationReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(registration.Token.IsCancellationRequested);

        try
        {
            var secondCancellation = Task.Run(() => registry.TryRequestCancellation(runId));

            Assert.True(await secondCancellation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(registration.Token.IsCancellationRequested);
        }
        finally
        {
            releaseFirstCancellation.TrySetResult(true);
        }

        Assert.True(await firstCancellation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(registration.Token.IsCancellationRequested);
        registration.Dispose();
        Assert.False(registry.TryRequestCancellation(runId));
    }

    [Fact]
    public async Task Disposal_WaitsForEveryActiveCancellationOperationBeforeDisposingSource()
    {
        var firstCancellationReserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancellationReserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondCancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var signallingOperations = 0;
        var registry = new ExecutionCancellationRegistry(() =>
        {
            switch (Interlocked.Increment(ref signallingOperations))
            {
                case 1:
                    firstCancellationReserved.SetResult(true);
                    releaseFirstCancellation.Task.GetAwaiter().GetResult();
                    break;
                case 2:
                    secondCancellationReserved.SetResult(true);
                    releaseSecondCancellation.Task.GetAwaiter().GetResult();
                    break;
            }
        });
        var runId = Guid.NewGuid();
        var registration = registry.Register(runId);
        var firstCancellation = Task.Run(() => registry.TryRequestCancellation(runId));

        await firstCancellationReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondCancellation = Task.Run(() => registry.TryRequestCancellation(runId));
        await secondCancellationReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        registration.Dispose();
        Assert.False(registry.TryRequestCancellation(runId));

        try
        {
            releaseFirstCancellation.SetResult(true);
            Assert.True(await firstCancellation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(registration.Token.IsCancellationRequested);

            releaseSecondCancellation.SetResult(true);
            Assert.True(await secondCancellation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseFirstCancellation.TrySetResult(true);
            releaseSecondCancellation.TrySetResult(true);
        }

        Assert.False(registry.TryRequestCancellation(runId));
    }

    private static async Task SimulateExecutionAsync(
        IExecutionCancellationRegistry registry,
        Guid runId,
        Func<CancellationToken, Task> executeAsync)
    {
        using var registration = registry.Register(runId);
        await executeAsync(registration.Token);
    }

    public enum TerminalOutcome
    {
        Success,
        Failure,
        Cancellation
    }
}
