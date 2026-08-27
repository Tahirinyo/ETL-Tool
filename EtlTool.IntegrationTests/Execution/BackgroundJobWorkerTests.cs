using System.Collections.Concurrent;
using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
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
    public async Task StopAsync_InterruptsQueuedPersistedRunAndCleansItsSourceOnce()
    {
        await using var harness = new WorkerHarness(expectedExecutions: 1, expectedDisposals: 1);
        var activeRun = new EtlRun { Id = Guid.NewGuid(), Status = EtlRunStatus.Running };
        var queuedRun = new EtlRun
        {
            Id = Guid.NewGuid(),
            Status = EtlRunStatus.Queued,
            StoredFilePath = "queued.upload"
        };
        var activeEntered = NewSignal();
        harness.Probe.Execute = async (job, _, token) =>
        {
            activeEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        await harness.StartAsync();
        harness.RunRepository.Add(activeRun);
        harness.RunRepository.Add(queuedRun);
        await harness.Queue.EnqueueAsync(new BackgroundJob(activeRun.Id), CancellationToken.None);
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Queue.EnqueueAsync(new BackgroundJob(queuedRun.Id), CancellationToken.None);

        await harness.StopAsync();
        await harness.RecoveryService.RecoverAbandonedQueuedRunAsync(
            new BackgroundJob(queuedRun.Id),
            CancellationToken.None);

        Assert.Equal(EtlRunStatus.Running, activeRun.Status);
        Assert.Equal(EtlRunStatus.Interrupted, queuedRun.Status);
        Assert.NotNull(queuedRun.CompletedAt);
        Assert.Contains("stopped", queuedRun.SystemError!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([queuedRun.Id], harness.SourceFiles.DeletedRunIds);
    }

    [Fact]
    public async Task StartAsync_ClassifiesStaleRunsAndCleansEachRecoveredSourceOnce()
    {
        await using var harness = new WorkerHarness(expectedExecutions: 0, expectedDisposals: 0);
        var queued = new EtlRun { Id = Guid.NewGuid(), Status = EtlRunStatus.Queued };
        var legacyRunning = new EtlRun
        {
            Id = Guid.NewGuid(),
            Status = EtlRunStatus.Running,
            ExecutionConfiguration = null,
            TotalRows = 6,
            ProcessedRows = 7,
            ValidRows = 4,
            InvalidRows = 1,
            FilteredRows = 1,
            DeduplicatedRows = 1,
            InsertedRows = 3,
            UpdatedRows = 1
        };
        var running = new EtlRun
        {
            Id = Guid.NewGuid(),
            Status = EtlRunStatus.Running,
            ExecutionConfiguration = ValidExecutionConfiguration(),
            ProcessedRows = 8,
            ValidRows = 5,
            InvalidRows = 1,
            FilteredRows = 1,
            DeduplicatedRows = 1,
            InsertedRows = 4,
            UpdatedRows = 1
        };
        var terminalRuns = new[]
        {
            Terminal(EtlRunStatus.Completed),
            Terminal(EtlRunStatus.PartiallyCompleted),
            Terminal(EtlRunStatus.Failed),
            Terminal(EtlRunStatus.Interrupted)
        };
        harness.RunRepository.Add(queued);
        harness.RunRepository.Add(legacyRunning);
        harness.RunRepository.Add(running);
        foreach (var terminal in terminalRuns)
        {
            harness.RunRepository.Add(terminal);
        }

        await harness.StartAsync();
        await harness.RecoveryService.RecoverStaleRunsAsync(CancellationToken.None);
        await harness.StopAsync();

        Assert.Equal(EtlRunStatus.Interrupted, queued.Status);
        Assert.NotNull(queued.CompletedAt);
        Assert.Equal(0, queued.TotalRows);
        Assert.Equal(EtlRunStatus.Failed, legacyRunning.Status);
        Assert.NotNull(legacyRunning.CompletedAt);
        Assert.Equal(
            "The admitted ETL execution configuration is unavailable.",
            legacyRunning.SystemError);
        Assert.Equal(6, legacyRunning.TotalRows);
        Assert.Equal((7L, 4L, 1L, 1L, 1L, 3L, 1L), Counters(legacyRunning));
        Assert.Equal(EtlRunStatus.Interrupted, running.Status);
        Assert.NotNull(running.CompletedAt);
        Assert.Equal(8, running.TotalRows);
        Assert.Equal((8L, 5L, 1L, 1L, 1L, 4L, 1L), Counters(running));
        Assert.Equal(
            new[]
            {
                EtlRunStatus.Completed,
                EtlRunStatus.PartiallyCompleted,
                EtlRunStatus.Failed,
                EtlRunStatus.Interrupted
            },
            terminalRuns.Select(run => run.Status));
        Assert.All(terminalRuns, terminal =>
        {
            Assert.Equal($"Historical {terminal.Status}.", terminal.SystemError);
            Assert.Equal(new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero), terminal.CompletedAt);
        });
        Assert.Equal(3, harness.SourceFiles.DeletedRunIds.Count);
        Assert.Equal(3, harness.SourceFiles.DeletedRunIds.Distinct().Count());
        Assert.Contains(queued.Id, harness.SourceFiles.DeletedRunIds);
        Assert.Contains(legacyRunning.Id, harness.SourceFiles.DeletedRunIds);
        Assert.Contains(running.Id, harness.SourceFiles.DeletedRunIds);
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

    private static EtlRun Terminal(EtlRunStatus status)
    {
        var diagnostic = $"Historical {status}.";
        return new EtlRun
        {
            Id = Guid.NewGuid(),
            Status = status,
            SystemError = diagnostic,
            CompletedAt = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero)
        };
    }

    private static (long, long, long, long, long, long, long) Counters(EtlRun run) =>
        (run.ProcessedRows, run.ValidRows, run.InvalidRows, run.FilteredRows,
            run.DeduplicatedRows, run.InsertedRows, run.UpdatedRows);

    private static EtlRunExecutionConfiguration ValidExecutionConfiguration() =>
        EtlRunExecutionConfiguration.Capture(new PipelineDefinition
        {
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions
            {
                CultureName = "en-US",
                FirstRowIsHeader = true
            },
            ExpectedSchema = [new SourceFieldDefinition { Name = "Id" }],
            FieldMappings = [new FieldMapping { SourceField = "Id", TargetField = "id" }],
            DestinationDatabase = "target_database",
            DestinationCollection = "target_collection",
            UpsertKeyField = "id"
        });

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
            RunRepository = new InMemoryRecoveryRunRepository();
            SourceFiles = new RecordingRunSourceFileStore();
            services.AddSingleton<IEtlRunRepository>(RunRepository);
            services.AddSingleton<IRunSourceFileStore>(SourceFiles);
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<AbandonedRunRecoveryService>();
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
            RecoveryService = _services.GetRequiredService<AbandonedRunRecoveryService>();
            _worker = Assert.Single(_services.GetServices<IHostedService>());
        }

        public InProcessBackgroundJobQueue Queue { get; }

        public IExecutionCancellationRegistry CancellationRegistry { get; }

        public InMemoryRecoveryRunRepository RunRepository { get; }

        public RecordingRunSourceFileStore SourceFiles { get; }

        public AbandonedRunRecoveryService RecoveryService { get; }

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

    private sealed class RecordingRunSourceFileStore : IRunSourceFileStore
    {
        public ConcurrentQueue<Guid> DeletedRunIds { get; } = new();

        public Stream Open(EtlRun run) => throw new NotSupportedException();

        public Task DeleteAsync(EtlRun run, CancellationToken cancellationToken)
        {
            DeletedRunIds.Enqueue(run.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRecoveryRunRepository : IEtlRunRepository
    {
        private readonly object _sync = new();
        private readonly List<EtlRun> _runs = [];

        public void Add(EtlRun run)
        {
            lock (_sync) _runs.Add(run);
        }

        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken)
        {
            lock (_sync) return Task.FromResult<EtlRun?>(_runs.SingleOrDefault(run => run.Id == runId));
        }

        public Task<IReadOnlyList<EtlRun>> ListNonTerminalAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<EtlRun>>(
                    _runs.Where(run => run.Status is EtlRunStatus.Queued or EtlRunStatus.Running).ToArray());
            }
        }

        public Task<bool> TryInterruptAsync(
            Guid runId,
            EtlRunStatus expectedStatus,
            DateTimeOffset completedAt,
            long observedRows,
            string systemError,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                var run = _runs.SingleOrDefault(run => run.Id == runId);
                if (run is null || run.Status != expectedStatus)
                {
                    return Task.FromResult(false);
                }

                run.Status = EtlRunStatus.Interrupted;
                run.CompletedAt = completedAt;
                run.TotalRows = Math.Max(run.TotalRows, observedRows);
                run.SystemError = systemError;
                run.ErrorReportPath = null;
                return Task.FromResult(true);
            }
        }

        public Task AddAsync(EtlRun run, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EtlRun>> ListByPipelineIdAsync(Guid pipelineId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryStartAsync(Guid runId, DateTimeOffset startedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
            Guid runId,
            DateTimeOffset completedAt,
            string systemError,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                var run = _runs.SingleOrDefault(run => run.Id == runId);
                if (run is null
                    || run.Status != EtlRunStatus.Running
                    || run.ExecutionConfiguration is not null)
                {
                    return Task.FromResult(false);
                }

                run.Status = EtlRunStatus.Failed;
                run.CompletedAt = completedAt;
                run.SystemError = systemError;
                run.ErrorReportPath = null;
                return Task.FromResult(true);
            }
        }
        public Task<bool> TryUpdateProgressAsync(Guid runId, BatchExecutionProgress progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryMarkTerminalAsync(Guid runId, EtlRunStatus status, DateTimeOffset completedAt, BatchExecutionProgress? finalProgress, string? systemError, string? errorReportPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

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
