using System.Net;
using System.Net.Http.Json;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Execution;
using EtlTool.Web.Controllers;
using EtlTool.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EtlTool.IntegrationTests.Web;

public sealed class RunAdmissionMvcIntegrationTests
{
    [Fact]
    public async Task Execute_PostAdmitsRealRunAndReturnsBeforeBackgroundJobCompletes()
    {
        var pipeline = Pipeline();
        await using var host = await RunAdmissionHost.StartAsync(pipeline);
        var token = await host.Client.GetFromJsonAsync<AntiforgeryTokenResponse>(
            "/ValidationAntiforgery");
        Assert.NotNull(token);
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>(
                "__RequestVerificationToken",
                token.RequestToken)
        ]);

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Execute",
            content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var run = Assert.Single(host.RunRepository.Runs);
        Assert.Equal(EtlRunStatus.Queued, run.Status);
        Assert.Equal(pipeline.Id, run.PipelineId);
        Assert.Equal("customers.csv", run.OriginalFileName);
        Assert.Equal(
            $"/Runs/{run.Id}?pipelineId={pipeline.Id}",
            response.Headers.Location?.OriginalString);
        Assert.Equal([run.Id], host.QueueJobs.Select(job => job.RunId));
        Assert.False(host.Executor.Completed.Task.IsCompleted);

        await host.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.Executor.Completed.Task.IsCompleted);
        using var status = await host.Client.GetAsync($"/Runs/{run.Id}/Status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        host.Executor.Release.TrySetResult();
        await host.Executor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Execute_PostAdmitsPostgreSqlRunWithoutWizardFileReservation()
    {
        var pipeline = PostgreSqlPipeline();
        await using var host = await RunAdmissionHost.StartAsync(pipeline, startWorker: false);
        var token = await host.Client.GetFromJsonAsync<AntiforgeryTokenResponse>(
            "/ValidationAntiforgery");
        Assert.NotNull(token);
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>(
                "__RequestVerificationToken",
                token.RequestToken)
        ]);

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Execute",
            content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var run = Assert.Single(host.RunRepository.Runs);
        Assert.Equal(EtlRunStatus.Queued, run.Status);
        Assert.Equal(string.Empty, run.OriginalFileName);
        Assert.Equal(string.Empty, run.StoredFilePath);
        Assert.Equal(0, host.SourceStore.ReservationCount);
        Assert.True(host.SourceStore.HasActiveSource);
        Assert.Equal(run.Id, Assert.Single(host.QueueJobs).RunId);
        Assert.Equal(
            $"/Runs/{run.Id}?pipelineId={pipeline.Id}",
            response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Execute_SaturatedQueueReturnsSafeFailureAndDoesNotRetainTimedOutJob()
    {
        var pipeline = Pipeline();
        var completedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(completedAt);
        await using var host = await RunAdmissionHost.StartAsync(
            pipeline,
            capacity: 1,
            startWorker: false,
            timeProvider);
        var retainedJob = new BackgroundJob(Guid.NewGuid());
        await host.BackgroundQueue.EnqueueAsync(retainedJob, CancellationToken.None);
        var token = await host.Client.GetFromJsonAsync<AntiforgeryTokenResponse>(
            "/ValidationAntiforgery");
        Assert.NotNull(token);
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>(
                "__RequestVerificationToken",
                token.RequestToken)
        ]);

        var responseTask = host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Execute",
            content);
        await host.AdmissionQueue.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(responseTask.IsCompleted);

        timeProvider.ExpireAdmissionDeadline();
        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains(
            "The run could not be admitted to background execution. Try again.",
            responseBody,
            StringComparison.Ordinal);
        var run = Assert.Single(host.RunRepository.Runs);
        Assert.Equal(EtlRunStatus.Interrupted, run.Status);
        Assert.Equal(completedAt, run.CompletedAt);
        Assert.Equal("The run could not be admitted to background execution.", run.SystemError);
        Assert.Empty(host.QueueJobs);
        Assert.True(host.SourceStore.HasActiveSource);
        Assert.Equal(1, host.SourceStore.RollbackCount);
        Assert.True(host.BackgroundQueue.TryRead(out var queuedJob));
        Assert.Equal(retainedJob, queuedJob);
        Assert.False(host.BackgroundQueue.TryRead(out _));
    }

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

    private sealed class RunAdmissionHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private RunAdmissionHost(
            WebApplication application,
            HttpClient client,
            InMemoryRunRepository runRepository,
            RecordingQueue queue,
            InProcessBackgroundJobQueue backgroundQueue,
            RunSourceStore sourceStore,
            BlockingExecutorProbe executor)
        {
            _application = application;
            Client = client;
            RunRepository = runRepository;
            AdmissionQueue = queue;
            BackgroundQueue = backgroundQueue;
            SourceStore = sourceStore;
            Executor = executor;
        }

        public HttpClient Client { get; }

        public InMemoryRunRepository RunRepository { get; }

        public IReadOnlyList<BackgroundJob> QueueJobs => AdmissionQueue.Jobs;

        public RecordingQueue AdmissionQueue { get; }

        public InProcessBackgroundJobQueue BackgroundQueue { get; }

        public RunSourceStore SourceStore { get; }

        public BlockingExecutorProbe Executor { get; }

        public static async Task<RunAdmissionHost> StartAsync(
            PipelineDefinition pipeline,
            int capacity = 4,
            bool startWorker = true,
            TimeProvider? timeProvider = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services
                .AddControllersWithViews()
                .AddApplicationPart(typeof(PipelinesController).Assembly);

            var runRepository = new InMemoryRunRepository();
            var inProcessQueue = new InProcessBackgroundJobQueue(
                new BackgroundJobQueueOptions { Capacity = capacity });
            var queue = new RecordingQueue(inProcessQueue);
            var sourceStore = new RunSourceStore(pipeline.Id);
            var executor = new BlockingExecutorProbe();
            builder.Services.AddSingleton<IPipelineService>(new SinglePipelineService(pipeline));
            builder.Services.AddSingleton<IPipelineReadinessService>(new ReadyReadinessService());
            builder.Services.AddSingleton(sourceStore);
            builder.Services.AddSingleton<IWizardSourceStore>(sourceStore);
            builder.Services.AddSingleton<PipelineSourceCommitCoordinator>();
            builder.Services.AddSingleton<IEtlRunRepository>(runRepository);
            builder.Services.AddSingleton<IRunSourceStore, NoOpRunSourceFileStore>();
            builder.Services.AddSingleton<AbandonedRunRecoveryService>();
            builder.Services.AddSingleton(queue);
            builder.Services.AddSingleton<IBackgroundJobQueue>(queue);
            builder.Services.AddSingleton(inProcessQueue);
            builder.Services.AddSingleton(new RunAdmissionOptions());
            builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
            builder.Services.AddSingleton<IExecutionCancellationRegistry, ExecutionCancellationRegistry>();
            builder.Services.AddSingleton(executor);
            builder.Services.AddScoped<IBackgroundJobExecutor, BlockingBackgroundJobExecutor>();
            builder.Services.AddScoped<IRunAdmissionService, RunAdmissionService>();
            if (startWorker)
            {
                builder.Services.AddHostedService<BackgroundJobWorker>();
            }

            var application = builder.Build();
            application.MapGet(
                "/ValidationAntiforgery",
                (HttpContext context, IAntiforgery antiforgery) =>
                    Results.Ok(new AntiforgeryTokenResponse(
                        antiforgery.GetAndStoreTokens(context).RequestToken!)));
            application.MapControllers();
            await application.StartAsync();

            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(address)
            };
            return new RunAdmissionHost(
                application,
                client,
                runRepository,
                queue,
                inProcessQueue,
                sourceStore,
                executor);
        }

        public async ValueTask DisposeAsync()
        {
            Executor.Release.TrySetResult();
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    public sealed record AntiforgeryTokenResponse(string RequestToken);

    private sealed class SinglePipelineService(PipelineDefinition pipeline) : IPipelineService
    {
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ReadyReadinessService : IPipelineReadinessService
    {
        public PipelineReadinessResult Evaluate(PipelineDefinition pipeline) => new([]);

        public Task<PipelineReadinessResult?> EvaluateAsync(
            Guid pipelineId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PipelineReadinessResult?>(new PipelineReadinessResult([]));
    }

    public sealed class RunSourceStore(Guid pipelineId) : IWizardSourceStore
    {
        private bool _active = true;
        private bool _reserved;

        public bool HasActiveSource => _active;

        public int RollbackCount { get; private set; }

        public int ReservationCount { get; private set; }

        public Task<IWizardRunSourceReservation?> ReserveForRunAsync(
            Guid requestedPipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            if (requestedPipelineId != pipelineId || !_active || _reserved)
            {
                return Task.FromResult<IWizardRunSourceReservation?>(null);
            }

            _reserved = true;
            ReservationCount++;
            return Task.FromResult<IWizardRunSourceReservation?>(new Reservation(this));
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

        private sealed class Reservation(RunSourceStore owner) : IWizardRunSourceReservation
        {
            private int _completed;

            public string OriginalFileName => "customers.csv";

            public string StoredFilePath => "C:\\trusted\\generated.upload";

            public void TransferToRun()
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    owner._reserved = false;
                    owner._active = false;
                }
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    owner._reserved = false;
                    owner.RollbackCount++;
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed class RecordingQueue(InProcessBackgroundJobQueue inner) : IBackgroundJobQueue
    {
        public List<BackgroundJob> Jobs { get; } = [];

        public TaskCompletionSource EnqueueStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken)
        {
            EnqueueStarted.TrySetResult();
            await inner.EnqueueAsync(job, cancellationToken);
            Jobs.Add(job);
        }
    }

    private sealed class InMemoryRunRepository : IEtlRunRepository
    {
        public List<EtlRun> Runs { get; } = [];

        public Task AddAsync(EtlRun run, CancellationToken cancellationToken)
        {
            Runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<EtlRun?>(Runs.SingleOrDefault(run => run.Id == runId));

        public Task<IReadOnlyList<EtlRun>> ListByPipelineIdAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EtlRun>>(
                Runs.Where(run => run.PipelineId == pipelineId).ToArray());

        public Task<IReadOnlyList<EtlRun>> ListNonTerminalAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EtlRun>>(
                Runs.Where(run => run.Status is EtlRunStatus.Queued or EtlRunStatus.Running).ToArray());

        public Task<bool> TryStartAsync(Guid runId, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
            Guid runId,
            DateTimeOffset completedAt,
            string systemError,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TryInterruptAsync(
            Guid runId,
            EtlRunStatus expectedStatus,
            DateTimeOffset completedAt,
            long observedRows,
            string systemError,
            CancellationToken cancellationToken)
        {
            var run = Runs.SingleOrDefault(item => item.Id == runId);
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

        public Task<bool> TryUpdateProgressAsync(Guid runId, BatchExecutionProgress progress, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> TryMarkTerminalAsync(
            Guid runId,
            EtlRunStatus status,
            DateTimeOffset completedAt,
            BatchExecutionProgress? finalProgress,
            string? systemError,
            string? errorReportPath,
            CancellationToken cancellationToken)
        {
            var run = Runs.SingleOrDefault(item => item.Id == runId);
            if (run is null || run.Status != EtlRunStatus.Queued)
            {
                return Task.FromResult(false);
            }

            run.Status = status;
            run.CompletedAt = completedAt;
            run.SystemError = systemError;
            run.ErrorReportPath = errorReportPath;
            return Task.FromResult(true);
        }
    }

    private sealed class BlockingExecutorProbe
    {
        public TaskCompletionSource Started { get; } = Signal();

        public TaskCompletionSource Release { get; } = Signal();

        public TaskCompletionSource Completed { get; } = Signal();

        private static TaskCompletionSource Signal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class NoOpRunSourceFileStore : IRunSourceStore
    {
        public Task<IEtlSource> OpenAsync(EtlRun run, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(EtlRun run, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class BlockingBackgroundJobExecutor(BlockingExecutorProbe probe) : IBackgroundJobExecutor
    {
        public async Task ExecuteAsync(BackgroundJob job, CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
            probe.Completed.TrySetResult();
        }
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
