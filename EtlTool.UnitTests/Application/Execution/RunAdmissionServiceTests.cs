using EtlTool.Application.Execution;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Execution;

public sealed class RunAdmissionServiceTests
{
    [Fact]
    public void Options_DefaultToFiveSecondsAndRejectOutOfRangeTimeouts()
    {
        var options = new RunAdmissionOptions();

        Assert.Equal(5_000, options.QueueAdmissionTimeoutMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(5), options.QueueAdmissionTimeout);
        options.Validate();

        new RunAdmissionOptions { QueueAdmissionTimeoutMilliseconds = 1 }.Validate();
        new RunAdmissionOptions
        {
            QueueAdmissionTimeoutMilliseconds =
                RunAdmissionOptions.MaximumQueueAdmissionTimeoutMilliseconds
        }.Validate();

        var zero = Assert.Throws<InvalidOperationException>(() =>
            new RunAdmissionOptions { QueueAdmissionTimeoutMilliseconds = 0 }.Validate());
        Assert.Contains(
            "RunAdmission:QueueAdmissionTimeoutMilliseconds",
            zero.Message,
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            new RunAdmissionOptions { QueueAdmissionTimeoutMilliseconds = -1 }.Validate());

        Assert.Throws<InvalidOperationException>(() =>
            new RunAdmissionOptions
            {
                QueueAdmissionTimeoutMilliseconds =
                    RunAdmissionOptions.MaximumQueueAdmissionTimeoutMilliseconds + 1
            }.Validate());
    }

    [Fact]
    public async Task AdmitAsync_ReadyPipelinePersistsQueuedRunAndEnqueuesExactlyOnce()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue();
        var service = Service(pipeline, store, repository, queue);

        var result = await service.AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Admitted, result.Status);
        var run = Assert.Single(repository.Runs);
        Assert.Equal(result.RunId, run.Id);
        Assert.Equal(pipeline.Id, run.PipelineId);
        Assert.Equal(pipeline.Name, run.PipelineName);
        Assert.Equal(EtlRunStatus.Queued, run.Status);
        Assert.Equal("customers.csv", run.OriginalFileName);
        Assert.Equal("C:\\safe\\generated.upload", run.StoredFilePath);
        Assert.NotNull(run.ExecutionConfiguration);
        Assert.Equal(pipeline.SourceType, run.ExecutionConfiguration.SourceType);
        Assert.Equal(pipeline.SourceOptions.CultureName, run.ExecutionConfiguration.SourceOptions.CultureName);
        Assert.Null(run.StartedAt);
        Assert.Null(run.CompletedAt);
        Assert.Equal(0, run.ProcessedRows);
        Assert.Equal(run.Id, Assert.Single(queue.Jobs).RunId);
        Assert.False(store.HasActiveSource);
        Assert.All(repository.AddTokens, token => Assert.False(token.CanBeCanceled));
        Assert.All(queue.Tokens, token => Assert.True(token.CanBeCanceled));
        Assert.All(queue.Tokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task AdmitAsync_PostgreSqlPersistsImmutableLogicalSourceWithoutFileReservationOrSecrets()
    {
        var pipeline = PostgreSqlPipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue();
        var service = Service(pipeline, store, repository, queue);

        var result = await service.AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Admitted, result.Status);
        var run = Assert.Single(repository.Runs);
        Assert.Equal(string.Empty, run.OriginalFileName);
        Assert.Equal(string.Empty, run.StoredFilePath);
        Assert.Equal(0, store.ReservationCount);
        Assert.True(store.HasActiveSource);
        Assert.Equal(run.Id, Assert.Single(queue.Jobs).RunId);
        var source = Assert.IsType<PostgreSqlSourceOptions>(run.ExecutionConfiguration!.PostgreSqlSource);
        Assert.Equal("ReportingDb", source.ConnectionProfile);
        Assert.Equal("reporting", source.Database);
        Assert.Equal("public", source.Schema);
        Assert.Equal("customers", source.Table);
        Assert.DoesNotContain(
            run.ExecutionConfiguration.GetType().GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));

        pipeline.PostgreSqlSource!.Table = "edited_after_admission";
        Assert.Equal("customers", source.Table);
    }

    [Fact]
    public async Task AdmitAsync_PostgreSqlRejectsSecondActiveRunWithoutFileReservation()
    {
        var pipeline = PostgreSqlPipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        repository.Runs.Add(new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = pipeline.Id,
            Status = EtlRunStatus.Running
        });
        var queue = new RecordingQueue();

        var result = await Service(pipeline, store, repository, queue)
            .AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.RunAlreadyActive, result.Status);
        Assert.Single(repository.Runs);
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, store.ReservationCount);
    }

    [Fact]
    public async Task AdmitAsync_PostgreSqlQueueFailureInterruptsRunWithoutFileRollback()
    {
        var pipeline = PostgreSqlPipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue { Failure = new IOException("Queue unavailable.") };

        var result = await Service(pipeline, store, repository, queue)
            .AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Failed, result.Status);
        Assert.Equal(EtlRunStatus.Interrupted, Assert.Single(repository.Runs).Status);
        Assert.Equal(0, store.ReservationCount);
        Assert.Equal(0, store.RollbackCount);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task AdmitAsync_ConcurrentPostsConsumeOneActiveSourceOnlyOnce()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue();
        var addEntered = Signal();
        var releaseAdd = Signal();
        repository.BeforeAddAsync = async cancellationToken =>
        {
            addEntered.TrySetResult();
            await releaseAdd.Task.WaitAsync(cancellationToken);
        };
        var service = Service(pipeline, store, repository, queue);

        var first = service.AdmitAsync(pipeline.Id, CancellationToken.None);
        await addEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.AdmitAsync(pipeline.Id, CancellationToken.None);
        Assert.False(second.IsCompleted);

        releaseAdd.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result.Status == RunAdmissionStatus.Admitted));
        Assert.Equal(1, results.Count(result => result.Status == RunAdmissionStatus.SourceUnavailable));
        Assert.Single(repository.Runs);
        Assert.Single(queue.Jobs);
    }

    [Fact]
    public async Task AdmitAsync_ReadinessProblemsIncludingSchemaAndRuleReferencesCreateNothing()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue();
        PipelineReadinessProblem[] problems =
        [
            new("Source", "The source schema requires remapping."),
            new("Transformation", "A rule references an unavailable mapped field.")
        ];
        var service = Service(
            pipeline,
            store,
            repository,
            queue,
            new PipelineReadinessResult(problems));

        var result = await service.AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.PipelineNotReady, result.Status);
        Assert.Equal(problems, result.ReadinessProblems);
        Assert.Empty(repository.Runs);
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, store.ReservationCount);
        Assert.True(store.HasActiveSource);
    }

    [Fact]
    public async Task AdmitAsync_UnavailableSourceCreatesNothing()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id) { HasActiveSource = false };
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue();

        var result = await Service(pipeline, store, repository, queue)
            .AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.SourceUnavailable, result.Status);
        Assert.Empty(repository.Runs);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task AdmitAsync_MissingPipelineCreatesNothing()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue();
        var service = new RunAdmissionService(
            new MissingPipelineService(),
            new StubReadinessService(new PipelineReadinessResult([])),
            new PipelineSourceCommitCoordinator(store),
            repository,
            queue,
            new RunAdmissionOptions(),
            TimeProvider.System);

        var result = await service.AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.PipelineNotFound, result.Status);
        Assert.Empty(repository.Runs);
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, store.ReservationCount);
    }

    [Fact]
    public async Task AdmitAsync_RequestCancellationAfterReservationDoesNotCancelCommittedAdmission()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        using var requestCancellation = new CancellationTokenSource();
        var repository = new RecordingRunRepository
        {
            BeforeAddAsync = _ =>
            {
                requestCancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        var queue = new RecordingQueue();

        var result = await Service(pipeline, store, repository, queue)
            .AdmitAsync(pipeline.Id, requestCancellation.Token);

        Assert.True(requestCancellation.IsCancellationRequested);
        Assert.Equal(RunAdmissionStatus.Admitted, result.Status);
        Assert.Single(repository.Runs);
        Assert.Single(queue.Jobs);
        Assert.All(repository.AddTokens, token => Assert.False(token.CanBeCanceled));
        Assert.All(queue.Tokens, token => Assert.True(token.CanBeCanceled));
        Assert.All(queue.Tokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task AdmitAsync_AdmissionDeadlineInterruptsRunRestoresSourceAndAllowsRetry()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new ControlledWaitingQueue();
        var completedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(completedAt);
        var service = Service(
            pipeline,
            store,
            repository,
            queue,
            timeProvider: timeProvider);

        var admission = service.AdmitAsync(pipeline.Id, CancellationToken.None);
        await queue.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(admission.IsCompleted);

        timeProvider.ExpireAdmissionDeadline();
        var failed = await admission.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunAdmissionStatus.Failed, failed.Status);
        Assert.IsAssignableFrom<OperationCanceledException>(failed.Failure);
        var interrupted = Assert.Single(repository.Runs);
        Assert.Equal(EtlRunStatus.Interrupted, interrupted.Status);
        Assert.Equal(completedAt, interrupted.CompletedAt);
        Assert.Equal("The run could not be admitted to background execution.", interrupted.SystemError);
        Assert.Empty(queue.Jobs);
        Assert.True(store.HasActiveSource);
        Assert.Equal(1, store.RollbackCount);
        Assert.All(repository.TerminalTokens, token => Assert.False(token.CanBeCanceled));

        queue.WaitForCancellation = false;
        var retried = await service.AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Admitted, retried.Status);
        Assert.Equal(2, repository.Runs.Count);
        Assert.Equal(retried.RunId, Assert.Single(queue.Jobs).RunId);
        Assert.False(store.HasActiveSource);
    }

    [Fact]
    public async Task AdmitAsync_EnqueueSuccessWinsWhenDeadlineSignalsBeforeContinuation()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var queue = new AcceptThenExpireQueue(timeProvider);

        var result = await Service(
                pipeline,
                store,
                repository,
                queue,
                timeProvider: timeProvider)
            .AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Admitted, result.Status);
        Assert.Equal(EtlRunStatus.Queued, Assert.Single(repository.Runs).Status);
        Assert.Equal(result.RunId, Assert.Single(queue.Jobs).RunId);
        Assert.False(store.HasActiveSource);
        Assert.Equal(0, store.RollbackCount);
    }

    [Fact]
    public async Task AdmitAsync_PersistenceFailureRestoresSourceAndDoesNotEnqueue()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository
        {
            AddFailure = new IOException("Persistence unavailable.")
        };
        var queue = new RecordingQueue();

        var result = await Service(pipeline, store, repository, queue)
            .AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Failed, result.Status);
        Assert.IsType<IOException>(result.Failure);
        Assert.Empty(repository.Runs);
        Assert.Empty(queue.Jobs);
        Assert.True(store.HasActiveSource);
        Assert.Equal(1, store.RollbackCount);
    }

    [Fact]
    public async Task AdmitAsync_QueueFailureInterruptsRunWithSafeErrorAndRestoresSource()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository();
        var queue = new RecordingQueue
        {
            Failure = new InvalidOperationException("Internal queue detail.")
        };
        var completedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

        var result = await Service(
                pipeline,
                store,
                repository,
                queue,
                timeProvider: new FixedTimeProvider(completedAt))
            .AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Failed, result.Status);
        var run = Assert.Single(repository.Runs);
        Assert.Equal(EtlRunStatus.Interrupted, run.Status);
        Assert.Equal(completedAt, run.CompletedAt);
        Assert.Equal("The run could not be admitted to background execution.", run.SystemError);
        Assert.DoesNotContain("Internal queue detail", run.SystemError, StringComparison.Ordinal);
        Assert.Empty(queue.Jobs);
        Assert.True(store.HasActiveSource);
        Assert.Equal(1, store.RollbackCount);
        Assert.All(repository.TerminalTokens, token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task AdmitAsync_CompensationFailureNeverReportsSuccessfulAdmission()
    {
        var pipeline = Pipeline();
        var store = new RecordingSourceStore(pipeline.Id);
        var repository = new RecordingRunRepository { TerminalTransitionSucceeds = false };
        var queue = new RecordingQueue { Failure = new IOException("Queue unavailable.") };

        var result = await Service(pipeline, store, repository, queue)
            .AdmitAsync(pipeline.Id, CancellationToken.None);

        Assert.Equal(RunAdmissionStatus.Failed, result.Status);
        var aggregate = Assert.IsType<AggregateException>(result.Failure);
        Assert.Contains(aggregate.InnerExceptions,
            exception => exception.Message.Contains("admission-failure transition", StringComparison.Ordinal));
        Assert.Equal(EtlRunStatus.Queued, Assert.Single(repository.Runs).Status);
        Assert.True(store.HasActiveSource);
        Assert.Empty(queue.Jobs);
    }

    private static RunAdmissionService Service(
        PipelineDefinition pipeline,
        RecordingSourceStore store,
        RecordingRunRepository repository,
        IBackgroundJobQueue queue,
        PipelineReadinessResult? readiness = null,
        TimeProvider? timeProvider = null,
        RunAdmissionOptions? options = null) =>
        new(
            new StubPipelineService(pipeline),
            new StubReadinessService(readiness ?? new PipelineReadinessResult([])),
            new PipelineSourceCommitCoordinator(store),
            repository,
            queue,
            options ?? new RunAdmissionOptions(),
            timeProvider ?? TimeProvider.System);

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Customers",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            CultureName = "en-US",
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        }
    };

    private static PipelineDefinition PostgreSqlPipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PostgreSQL Customers",
        SourceType = SourceType.PostgreSql,
        SourceOptions = new SourceOptions { CultureName = "en-US" },
        PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        }
    };

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StubPipelineService(PipelineDefinition pipeline) : IPipelineService
    {
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);
        }

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MissingPipelineService : IPipelineService
    {
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(null);

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReadinessService(PipelineReadinessResult result) : IPipelineReadinessService
    {
        public PipelineReadinessResult Evaluate(PipelineDefinition pipeline) => result;

        public Task<PipelineReadinessResult?> EvaluateAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineReadinessResult?>(result);
    }

    private sealed class RecordingSourceStore(Guid pipelineId) : IWizardSourceStore
    {
        private readonly object _sync = new();
        private bool _reserved;
        private bool _hasActiveSource = true;

        public bool HasActiveSource
        {
            get
            {
                lock (_sync) return _hasActiveSource;
            }
            set
            {
                lock (_sync) _hasActiveSource = value;
            }
        }

        public int ReservationCount { get; private set; }

        public int RollbackCount { get; private set; }

        public Task<IWizardRunSourceReservation?> ReserveForRunAsync(
            Guid requestedPipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (requestedPipelineId != pipelineId || !_hasActiveSource || _reserved)
                {
                    return Task.FromResult<IWizardRunSourceReservation?>(null);
                }

                _reserved = true;
                ReservationCount++;
                return Task.FromResult<IWizardRunSourceReservation?>(new Reservation(this));
            }
        }

        public Task<bool> ActivateAsync(Guid id, Guid referenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid referenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IWizardSourceLease?> AcquireAsync(Guid id, SourceType type, SourceOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class Reservation(RecordingSourceStore owner) : IWizardRunSourceReservation
        {
            private int _completed;

            public string OriginalFileName => "customers.csv";

            public string StoredFilePath => "C:\\safe\\generated.upload";

            public void TransferToRun()
            {
                if (Interlocked.Exchange(ref _completed, 1) != 0) return;
                lock (owner._sync)
                {
                    owner._reserved = false;
                    owner._hasActiveSource = false;
                }
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    lock (owner._sync)
                    {
                        owner._reserved = false;
                        owner.RollbackCount++;
                    }
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingRunRepository : IEtlRunRepository
    {
        private readonly object _sync = new();

        public List<EtlRun> Runs { get; } = [];

        public List<CancellationToken> AddTokens { get; } = [];

        public List<CancellationToken> TerminalTokens { get; } = [];

        public Exception? AddFailure { get; init; }

        public bool TerminalTransitionSucceeds { get; init; } = true;

        public Func<CancellationToken, Task>? BeforeAddAsync { get; set; }

        public async Task AddAsync(EtlRun run, CancellationToken cancellationToken)
        {
            AddTokens.Add(cancellationToken);
            if (BeforeAddAsync is not null)
            {
                await BeforeAddAsync(cancellationToken);
            }

            if (AddFailure is not null) throw AddFailure;
            lock (_sync) Runs.Add(run);
        }

        public Task<bool> TryMarkTerminalAsync(
            Guid runId,
            EtlRunStatus status,
            DateTimeOffset completedAt,
            BatchExecutionProgress? finalProgress,
            string? systemError,
            string? errorReportPath,
            CancellationToken cancellationToken)
        {
            TerminalTokens.Add(cancellationToken);
            if (!TerminalTransitionSucceeds) return Task.FromResult(false);
            lock (_sync)
            {
                var run = Runs.Single(run => run.Id == runId);
                run.Status = status;
                run.CompletedAt = completedAt;
                run.SystemError = systemError;
                run.ErrorReportPath = errorReportPath;
            }

            return Task.FromResult(true);
        }

        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EtlRun>> ListByPipelineIdAsync(Guid id, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<EtlRun>>(
                    Runs.Where(run => run.PipelineId == id).ToArray());
            }
        }

        public Task<IReadOnlyList<EtlRun>> ListNonTerminalAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryStartAsync(Guid runId, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
            Guid runId,
            DateTimeOffset completedAt,
            string systemError,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryInterruptAsync(
            Guid runId,
            EtlRunStatus expectedStatus,
            DateTimeOffset completedAt,
            long observedRows,
            string systemError,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryUpdateProgressAsync(Guid runId, BatchExecutionProgress progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingQueue : IBackgroundJobQueue
    {
        public List<BackgroundJob> Jobs { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Exception? Failure { get; init; }

        public ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            if (Failure is not null) return ValueTask.FromException(Failure);
            Jobs.Add(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledWaitingQueue : IBackgroundJobQueue
    {
        public TaskCompletionSource EnqueueStarted { get; } = Signal();

        public List<BackgroundJob> Jobs { get; } = [];

        public bool WaitForCancellation { get; set; } = true;

        public async ValueTask EnqueueAsync(
            BackgroundJob job,
            CancellationToken cancellationToken)
        {
            EnqueueStarted.TrySetResult();
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Jobs.Add(job);
        }
    }

    private sealed class AcceptThenExpireQueue(ManualTimeProvider timeProvider) :
        IBackgroundJobQueue
    {
        public List<BackgroundJob> Jobs { get; } = [];

        public ValueTask EnqueueAsync(
            BackgroundJob job,
            CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            timeProvider.ExpireAdmissionDeadline();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _sync = new();
        private ManualTimer? _admissionTimer;

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_sync)
            {
                _admissionTimer = timer;
            }

            return timer;
        }

        public void ExpireAdmissionDeadline()
        {
            ManualTimer timer;
            lock (_sync)
            {
                timer = _admissionTimer
                    ?? throw new InvalidOperationException("The admission timer was not created.");
            }

            timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                Volatile.Read(ref _disposed) == 0;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback(state);
                }
            }
        }
    }
}
