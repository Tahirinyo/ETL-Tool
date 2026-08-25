using System.Collections.Concurrent;
using EtlTool.Application.Execution;
using EtlTool.Infrastructure.Execution;
using EtlTool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EtlTool.IntegrationTests.Execution;

public sealed class BackgroundJobWorkerTests
{
    [Fact]
    public async Task Worker_ProcessesFifoWithDistinctDisposedScopesAndIgnoresFormerAdmissionToken()
    {
        await using var harness = new WorkerHarness(expectedExecutions: 3, expectedDisposals: 3);
        var jobs = new[]
        {
            new BackgroundJob(Guid.NewGuid()),
            new BackgroundJob(Guid.NewGuid()),
            new BackgroundJob(Guid.NewGuid())
        };
        using var admissionCancellation = new CancellationTokenSource();

        foreach (var job in jobs)
        {
            await harness.Queue.EnqueueAsync(job, admissionCancellation.Token);
        }

        admissionCancellation.Cancel();
        await harness.StartAsync();
        await harness.Probe.ExecutionsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Probe.DisposalsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.StopAsync();

        Assert.Equal(jobs.Select(job => job.RunId), harness.Probe.Executions.Select(item => item.RunId));
        Assert.All(harness.Probe.Executions, item => Assert.False(item.TokenWasCancelled));
        Assert.Equal(3, harness.Probe.Executions.Select(item => item.ScopeId).Distinct().Count());
        Assert.Equal(
            harness.Probe.Executions.Select(item => item.ScopeId).Order(),
            harness.Probe.DisposedScopeIds.Order());
        Assert.All(jobs, job =>
            Assert.False(harness.CancellationRegistry.TryRequestCancellation(job.RunId)));
    }

    [Fact]
    public async Task Worker_LogsJobFailureAndContinuesWithFollowingJob()
    {
        await using var harness = new WorkerHarness(expectedExecutions: 2, expectedDisposals: 2);
        var failedRunId = Guid.NewGuid();
        var followingRunId = Guid.NewGuid();
        var expectedFailure = new InvalidOperationException("Simulated job failure.");
        harness.Probe.Execute = (job, _, _) =>
            job.RunId == failedRunId ? Task.FromException(expectedFailure) : Task.CompletedTask;

        await harness.Queue.EnqueueAsync(new BackgroundJob(failedRunId), CancellationToken.None);
        await harness.Queue.EnqueueAsync(new BackgroundJob(followingRunId), CancellationToken.None);
        await harness.StartAsync();
        await harness.Probe.DisposalsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.StopAsync();

        Assert.Equal([failedRunId, followingRunId], harness.Probe.Executions.Select(item => item.RunId));
        var failureLog = Assert.Single(harness.Logs.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains(failedRunId.ToString(), StringComparison.Ordinal));
        Assert.Same(expectedFailure, failureLog.Exception);
        Assert.False(harness.CancellationRegistry.TryRequestCancellation(failedRunId));
        Assert.False(harness.CancellationRegistry.TryRequestCancellation(followingRunId));
    }

    [Fact]
    public async Task Worker_LinksRunCancellationAndCleansRegistrationBeforeContinuing()
    {
        await using var harness = new WorkerHarness(expectedExecutions: 2, expectedDisposals: 2);
        var cancelledRunId = Guid.NewGuid();
        var followingRunId = Guid.NewGuid();
        var activeEntered = NewSignal();
        var cancellationObserved = NewSignal();
        harness.Probe.Execute = async (job, _, token) =>
        {
            if (job.RunId != cancelledRunId)
            {
                return;
            }

            activeEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        };

        await harness.Queue.EnqueueAsync(new BackgroundJob(cancelledRunId), CancellationToken.None);
        await harness.StartAsync();
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(harness.CancellationRegistry.TryRequestCancellation(cancelledRunId));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => !harness.CancellationRegistry.TryRequestCancellation(cancelledRunId),
            TimeSpan.FromSeconds(5));

        await harness.Queue.EnqueueAsync(new BackgroundJob(followingRunId), CancellationToken.None);
        await harness.Probe.DisposalsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.StopAsync();

        Assert.Equal([cancelledRunId, followingRunId], harness.Probe.Executions.Select(item => item.RunId));
        Assert.Contains(harness.Logs.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains(cancelledRunId.ToString(), StringComparison.Ordinal)
            && entry.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StopAsync_CancelsAndAwaitsActiveJobWithoutStartingQueuedJob()
    {
        await using var harness = new WorkerHarness(expectedExecutions: 1, expectedDisposals: 1);
        var activeRunId = Guid.NewGuid();
        var queuedRunId = Guid.NewGuid();
        var activeEntered = NewSignal();
        var cancellationObserved = NewSignal();
        harness.Probe.Execute = async (job, _, token) =>
        {
            if (job.RunId != activeRunId)
            {
                return;
            }

            activeEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        };

        await harness.Queue.EnqueueAsync(new BackgroundJob(activeRunId), CancellationToken.None);
        await harness.Queue.EnqueueAsync(new BackgroundJob(queuedRunId), CancellationToken.None);
        await harness.StartAsync();
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.StopAsync();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<BackgroundJobQueueClosedException>(() =>
            harness.Queue
                .EnqueueAsync(new BackgroundJob(Guid.NewGuid()), CancellationToken.None)
                .AsTask());
        Assert.Equal([activeRunId], harness.Probe.Executions.Select(item => item.RunId));
        Assert.Single(harness.Probe.DisposedScopeIds);
        Assert.False(harness.CancellationRegistry.TryRequestCancellation(activeRunId));
        Assert.Contains(harness.Logs.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(queuedRunId.ToString(), StringComparison.Ordinal)
            && entry.Message.Contains("abandoned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Worker_LogsBothCallbackAndScopeDisposalFailures()
    {
        await using var harness = new WorkerHarness(expectedExecutions: 1, expectedDisposals: 1);
        var runId = Guid.NewGuid();
        var callbackFailure = new InvalidOperationException("Simulated callback failure.");
        var disposalFailure = new IOException("Simulated scope disposal failure.");
        harness.Probe.Execute = (_, _, _) => Task.FromException(callbackFailure);
        harness.Probe.DisposeFailure = _ => disposalFailure;

        await harness.Queue.EnqueueAsync(new BackgroundJob(runId), CancellationToken.None);
        await harness.StartAsync();
        await harness.Probe.DisposalsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.StopAsync();

        var failureLog = Assert.Single(harness.Logs.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains(runId.ToString(), StringComparison.Ordinal));
        var aggregate = Assert.IsType<AggregateException>(failureLog.Exception);
        Assert.Contains(callbackFailure, aggregate.InnerExceptions);
        Assert.Contains(disposalFailure, aggregate.InnerExceptions);
        Assert.False(harness.CancellationRegistry.TryRequestCancellation(runId));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }

    private sealed class WorkerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IHostedService _worker;
        private bool _started;
        private bool _stopped;

        public WorkerHarness(int expectedExecutions, int expectedDisposals)
        {
            Probe = new JobExecutionProbe(expectedExecutions, expectedDisposals);
            Logs = new RecordingLoggerProvider();
            var services = new ServiceCollection();
            services.AddSingleton(new BackgroundJobQueueOptions { Capacity = 10 });
            services.AddSingleton<InProcessBackgroundJobQueue>();
            services.AddSingleton<IBackgroundJobQueue>(provider =>
                provider.GetRequiredService<InProcessBackgroundJobQueue>());
            services.AddSingleton<IExecutionCancellationRegistry, ExecutionCancellationRegistry>();
            services.AddSingleton(Probe);
            services.AddScoped<IBackgroundJobExecutor, ProbeBackgroundJobExecutor>();
            services.AddLogging(builder => builder.AddProvider(Logs));
            services.AddHostedService<BackgroundJobWorker>();

            _services = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
            Queue = _services.GetRequiredService<InProcessBackgroundJobQueue>();
            CancellationRegistry = _services.GetRequiredService<IExecutionCancellationRegistry>();
            _worker = Assert.Single(_services.GetServices<IHostedService>());
        }

        public InProcessBackgroundJobQueue Queue { get; }

        public IExecutionCancellationRegistry CancellationRegistry { get; }

        public JobExecutionProbe Probe { get; }

        public RecordingLoggerProvider Logs { get; }

        public async Task StartAsync()
        {
            await _worker.StartAsync(CancellationToken.None);
            _started = true;
        }

        public async Task StopAsync()
        {
            await _worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            _stopped = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_started && !_stopped)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _worker.StopAsync(cancellation.Token);
            }

            await _services.DisposeAsync();
            Logs.Dispose();
        }
    }

    private sealed class ProbeBackgroundJobExecutor(JobExecutionProbe probe) :
        IBackgroundJobExecutor,
        IAsyncDisposable
    {
        private readonly Guid _scopeId = Guid.NewGuid();

        public async Task ExecuteAsync(BackgroundJob job, CancellationToken cancellationToken)
        {
            probe.RecordExecution(job.RunId, _scopeId, cancellationToken.IsCancellationRequested);
            await probe.Execute(job, _scopeId, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            probe.RecordDisposal(_scopeId);
            var failure = probe.DisposeFailure(_scopeId);
            return failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(failure);
        }
    }

    private sealed class JobExecutionProbe
    {
        private readonly int _expectedExecutions;
        private readonly int _expectedDisposals;
        private int _executionCount;
        private int _disposalCount;

        public JobExecutionProbe(int expectedExecutions, int expectedDisposals)
        {
            _expectedExecutions = expectedExecutions;
            _expectedDisposals = expectedDisposals;
        }

        public ConcurrentQueue<ExecutionRecord> Executions { get; } = new();

        public ConcurrentQueue<Guid> DisposedScopeIds { get; } = new();

        public TaskCompletionSource ExecutionsReached { get; } = NewSignal();

        public TaskCompletionSource DisposalsReached { get; } = NewSignal();

        public Func<BackgroundJob, Guid, CancellationToken, Task> Execute { get; set; } =
            static (_, _, _) => Task.CompletedTask;

        public Func<Guid, Exception?> DisposeFailure { get; set; } = static _ => null;

        public void RecordExecution(Guid runId, Guid scopeId, bool tokenWasCancelled)
        {
            Executions.Enqueue(new ExecutionRecord(runId, scopeId, tokenWasCancelled));
            if (Interlocked.Increment(ref _executionCount) == _expectedExecutions)
            {
                ExecutionsReached.TrySetResult();
            }
        }

        public void RecordDisposal(Guid scopeId)
        {
            DisposedScopeIds.Enqueue(scopeId);
            if (Interlocked.Increment(ref _disposalCount) == _expectedDisposals)
            {
                DisposalsReached.TrySetResult();
            }
        }
    }

    private sealed record ExecutionRecord(Guid RunId, Guid ScopeId, bool TokenWasCancelled);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
