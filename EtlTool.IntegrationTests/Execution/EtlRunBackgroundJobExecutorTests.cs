using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.Execution;

namespace EtlTool.IntegrationTests.Execution;

public sealed class EtlRunBackgroundJobExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SuccessPersistsProgressAndCompletedStatus()
    {
        var harness = Harness();
        harness.Loader.Result = new BatchLoadResult(1, 0);

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(EtlRunStatus.Completed, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal(0, harness.Runs.TerminalProgress.UpdatedRows);
        Assert.Null(harness.Runs.SystemError);
        Assert.Equal(1, harness.Runs.ProgressUpdates);
    }

    [Fact]
    public async Task ExecuteAsync_FailureBeforeOrAfterConfirmedWorkUsesCorrectTerminalStatus()
    {
        var before = Harness();
        before.Orchestrator.Execute = (_, _, _, _) =>
            Task.FromException<BatchExecutionResult>(new InvalidOperationException("Before load."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            before.Executor.ExecuteAsync(new BackgroundJob(before.Run.Id), CancellationToken.None));

        Assert.Equal(EtlRunStatus.Failed, before.Runs.TerminalStatus);
        Assert.Null(before.Runs.TerminalProgress);

        var after = Harness();
        after.Loader.Result = new BatchLoadResult(1, 0);
        after.Orchestrator.Execute = async (load, report, _, token) =>
        {
            var loaded = await load([Row()], token);
            await report(Progress(1, 1, loaded.InsertedRows, loaded.UpdatedRows), token);
            throw new InvalidOperationException("After load.");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            after.Executor.ExecuteAsync(new BackgroundJob(after.Run.Id), CancellationToken.None));

        Assert.Equal(EtlRunStatus.PartiallyCompleted, after.Runs.TerminalStatus);
        Assert.Equal(1, after.Runs.TerminalProgress!.InsertedRows);
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailingBatchIsCountedOnceAndPartiallyCompleted()
    {
        var harness = Harness();
        var partialProgress = Progress(2, 2, insertedRows: 1, updatedRows: 0);
        var loadFailure = new BatchLoadException(
            "Partial batch.",
            new BatchLoadResult(1, 0),
            new IOException("Simulated write failure."));
        harness.Orchestrator.Execute = (_, _, _, _) =>
            Task.FromException<BatchExecutionResult>(
                new BatchExecutionException(partialProgress, loadFailure));

        await Assert.ThrowsAsync<BatchExecutionException>(() =>
            harness.Executor.ExecuteAsync(new BackgroundJob(harness.Run.Id), CancellationToken.None));

        Assert.Equal(EtlRunStatus.PartiallyCompleted, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal(0, harness.Runs.ProgressUpdates);
        Assert.Equal("MongoDB batch loading failed.", harness.Runs.SystemError);
    }

    [Fact]
    public async Task ExecuteAsync_ProgressPersistenceFailureRetainsAcknowledgedCountersForTerminalUpdate()
    {
        var harness = Harness();
        harness.Loader.Result = new BatchLoadResult(1, 0);
        harness.Runs.AcceptProgress = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.ExecuteAsync(new BackgroundJob(harness.Run.Id), CancellationToken.None));

        Assert.Equal(EtlRunStatus.PartiallyCompleted, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal(1, harness.Runs.ProgressUpdates);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationPersistsInterruptedWithConfirmedCounters()
    {
        var harness = Harness();
        using var cancellation = new CancellationTokenSource();
        var progress = Progress(1, 1, insertedRows: 1, updatedRows: 0);
        harness.Orchestrator.Execute = (_, _, _, token) =>
        {
            cancellation.Cancel();
            return Task.FromException<BatchExecutionResult>(
                new BatchExecutionCanceledException(
                    progress,
                    new OperationCanceledException(token),
                    token));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                cancellation.Token));

        Assert.Equal(EtlRunStatus.Interrupted, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal("ETL execution was interrupted.", harness.Runs.SystemError);
    }

    private static TestHarness Harness()
    {
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            Status = EtlRunStatus.Queued,
            StoredFilePath = "generated.upload"
        };
        var pipeline = new PipelineDefinition { Id = run.PipelineId };
        var runs = new StubRunRepository(run);
        var pipelines = new StubPipelineRepository(pipeline);
        var orchestrator = new StubOrchestrator();
        var loader = new StubLoader();
        var executor = new EtlRunBackgroundJobExecutor(
            runs,
            pipelines,
            orchestrator,
            loader,
            new FixedTimeProvider(),
            new MemorySourceFactory());
        return new TestHarness(run, runs, orchestrator, loader, executor);
    }

    private static BatchExecutionProgress Progress(
        long processedRows,
        long validRows,
        long insertedRows,
        long updatedRows) => new(
            processedRows,
            validRows,
            invalidRows: 0,
            filteredRows: 0,
            deduplicatedRows: 0,
            isCompleted: true,
            insertedRows,
            updatedRows);

    private static DataRow Row()
    {
        var row = new DataRow { SourceRowNumber = 2 };
        row.Values.Add("id", "A");
        return row;
    }

    private sealed record TestHarness(
        EtlRun Run,
        StubRunRepository Runs,
        StubOrchestrator Orchestrator,
        StubLoader Loader,
        EtlRunBackgroundJobExecutor Executor);

    private sealed class StubRunRepository(EtlRun run) : IEtlRunRepository
    {
        public bool AcceptProgress { get; set; } = true;

        public int ProgressUpdates { get; private set; }

        public EtlRunStatus? TerminalStatus { get; private set; }

        public BatchExecutionProgress? TerminalProgress { get; private set; }

        public string? SystemError { get; private set; }

        public Task AddAsync(EtlRun value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<EtlRun?>(runId == run.Id ? run : null);

        public Task<bool> TryStartAsync(
            Guid runId,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken)
        {
            run.Status = EtlRunStatus.Running;
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateProgressAsync(
            Guid runId,
            BatchExecutionProgress progress,
            CancellationToken cancellationToken)
        {
            ProgressUpdates++;
            return Task.FromResult(AcceptProgress);
        }

        public Task<bool> TryMarkTerminalAsync(
            Guid runId,
            EtlRunStatus status,
            DateTimeOffset completedAt,
            BatchExecutionProgress? finalProgress,
            string? systemError,
            CancellationToken cancellationToken)
        {
            TerminalStatus = status;
            TerminalProgress = finalProgress;
            SystemError = systemError;
            run.Status = status;
            return Task.FromResult(true);
        }
    }

    private sealed class StubPipelineRepository(PipelineDefinition pipeline) : IPipelineDefinitionRepository
    {
        public Task AddAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubOrchestrator : IBatchOrchestrator
    {
        public Func<
            Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>>,
            Func<BatchExecutionProgress, CancellationToken, Task>,
            Stream,
            CancellationToken,
            Task<BatchExecutionResult>> Execute { get; set; } = DefaultExecute;

        public Task<BatchExecutionResult> ExecuteAsync(
            Stream source,
            PipelineDefinition pipeline,
            Func<IReadOnlyList<DataRow>, CancellationToken, Task> processBatchAsync,
            Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
            Stream source,
            PipelineDefinition pipeline,
            Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> processBatchAsync,
            Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
            CancellationToken cancellationToken) =>
            Execute(processBatchAsync, reportProgressAsync, source, cancellationToken);

        private static async Task<BatchExecutionResult> DefaultExecute(
            Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> load,
            Func<BatchExecutionProgress, CancellationToken, Task> report,
            Stream source,
            CancellationToken cancellationToken)
        {
            var loaded = await load([Row()], cancellationToken);
            var progress = Progress(1, 1, loaded.InsertedRows, loaded.UpdatedRows);
            await report(progress, cancellationToken);
            return new BatchExecutionResult(1, 1, 0, 0, 0, loaded.InsertedRows, loaded.UpdatedRows);
        }
    }

    private sealed class StubLoader : IDataLoader
    {
        public BatchLoadResult Result { get; set; } = BatchLoadResult.Empty;

        public Task<BatchLoadResult> UpsertBatchAsync(
            IReadOnlyList<DataRow> rows,
            Application.MongoDB.MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken) => Task.FromResult(Result);
    }

    private sealed class MemorySourceFactory : IRunSourceStreamFactory
    {
        public Stream Open(EtlRun run) => new MemoryStream([1]);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    }
}
