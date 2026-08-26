using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Application.Reporting;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Execution;
using EtlTool.Infrastructure.Reporting;
using Microsoft.Extensions.Logging.Abstractions;

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
        Assert.Equal(0, harness.Output.OpenCount);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
        Assert.True(harness.SourceFiles.WasDisposedBeforeDelete);
    }

    [Fact]
    public async Task ExecuteAsync_FailureBeforeOrAfterConfirmedWorkUsesCorrectTerminalStatus()
    {
        var before = Harness();
        before.Orchestrator.Execute = (_, _, _, _, _) =>
            Task.FromException<BatchExecutionResult>(new InvalidOperationException("Before load."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            before.Executor.ExecuteAsync(new BackgroundJob(before.Run.Id), CancellationToken.None));

        Assert.Equal(EtlRunStatus.Failed, before.Runs.TerminalStatus);
        Assert.Null(before.Runs.TerminalProgress);
        Assert.Equal(1, before.SourceFiles.DeleteCount);

        var after = Harness();
        after.Loader.Result = new BatchLoadResult(1, 0);
        after.Orchestrator.Execute = async (load, _, report, _, token) =>
        {
            var loaded = await load([Row()], token);
            await report(Progress(1, 1, loaded.InsertedRows, loaded.UpdatedRows), token);
            throw new InvalidOperationException("After load.");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            after.Executor.ExecuteAsync(new BackgroundJob(after.Run.Id), CancellationToken.None));

        Assert.Equal(EtlRunStatus.PartiallyCompleted, after.Runs.TerminalStatus);
        Assert.Equal(1, after.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal(1, after.SourceFiles.DeleteCount);
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
        harness.Orchestrator.Execute = (_, _, _, _, _) =>
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
        harness.Orchestrator.Execute = (_, _, _, _, token) =>
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
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_StreamsInvalidFullRunResultsThroughCsvWriter()
    {
        var harness = Harness();
        harness.Pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Name" },
            new SourceFieldDefinition { Name = "Email" }
        ];
        harness.Orchestrator.Execute = async (_, reportInvalid, reportProgress, _, token) =>
        {
            await reportInvalid(InvalidResult(), token);
            var progress = new BatchExecutionProgress(
                processedRows: 1,
                validRows: 0,
                invalidRows: 1,
                filteredRows: 0,
                deduplicatedRows: 0,
                isCompleted: true);
            await reportProgress(progress, token);
            return new BatchExecutionResult(1, 0, 1, 0, 0);
        };

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(1, harness.Output.OpenCount);
        var csv = System.Text.Encoding.UTF8.GetString(harness.Output.LastStream!.ToArray());
        Assert.Contains("RunId,SourceRowNumber,ErrorStage,ErrorField", csv);
        Assert.Contains("Name is required.", csv);
        Assert.Contains("Email is invalid.", csv);
        Assert.Contains("Ada", csv);
        Assert.Equal(EtlRunStatus.Completed, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.Runs.TerminalProgress!.InvalidRows);
        Assert.Equal($"error-report-{harness.Run.Id:N}.csv", harness.Run.ErrorReportPath);
    }

    [Fact]
    public async Task ExecuteAsync_FinalizesHealthyInvalidRowReportBeforeLaterSystemFailure()
    {
        var harness = Harness();
        harness.Pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
        harness.Orchestrator.Execute = async (_, reportInvalid, _, _, token) =>
        {
            await reportInvalid(InvalidResult(), token);
            throw new IOException("The source became unavailable after this row.");
        };

        await Assert.ThrowsAsync<IOException>(() => harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id), CancellationToken.None));

        Assert.Equal(EtlRunStatus.Failed, harness.Runs.TerminalStatus);
        Assert.Equal($"error-report-{harness.Run.Id:N}.csv", harness.Run.ErrorReportPath);
        Assert.Equal(1, harness.Output.OpenCount);
    }

    [Fact]
    public async Task ExecuteAsync_SourceCleanupFailurePreservesCompletedTerminalStatus()
    {
        var harness = Harness();
        harness.SourceFiles.ThrowOnDelete = true;

        await harness.Executor.ExecuteAsync(new BackgroundJob(harness.Run.Id), CancellationToken.None);

        Assert.Equal(EtlRunStatus.Completed, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Theory]
    [InlineData(false, EtlRunStatus.Failed)]
    [InlineData(true, EtlRunStatus.PartiallyCompleted)]
    public async Task ExecuteAsync_ReportFailureUsesConfirmedWorkForTerminalStatus(
        bool commitBeforeFailure,
        EtlRunStatus expectedStatus)
    {
        var harness = Harness(new FailingErrorReportWriter());
        harness.Pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Name" },
            new SourceFieldDefinition { Name = "Email" }
        ];
        harness.Loader.Result = new BatchLoadResult(1, 0);
        harness.Orchestrator.Execute = async (load, reportInvalid, reportProgress, _, token) =>
        {
            if (commitBeforeFailure)
            {
                var loaded = await load([Row()], token);
                await reportProgress(
                    Progress(1, 1, loaded.InsertedRows, loaded.UpdatedRows),
                    token);
            }

            await reportInvalid(InvalidResult(), token);
            throw new InvalidOperationException("The reporting callback unexpectedly returned.");
        };

        await Assert.ThrowsAsync<ErrorReportGenerationException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                CancellationToken.None));

        Assert.Equal(expectedStatus, harness.Runs.TerminalStatus);
        Assert.Equal("Error report generation failed.", harness.Runs.SystemError);
        Assert.Equal(commitBeforeFailure ? 1 : 0, harness.Runs.TerminalProgress?.InsertedRows ?? 0);
    }

    [Fact]
    public async Task ExecuteAsync_AwaitsCsvConsumerBackpressureBeforeContinuingRun()
    {
        var writer = new BlockingErrorReportWriter();
        var harness = Harness(writer);
        harness.Pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
        var callbackReturned = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Orchestrator.Execute = async (_, reportInvalid, reportProgress, _, token) =>
        {
            await reportInvalid(InvalidResult(), token);
            callbackReturned.SetResult(true);
            var progress = new BatchExecutionProgress(1, 0, 1, 0, 0, isCompleted: true);
            await reportProgress(progress, token);
            return new BatchExecutionResult(1, 0, 1, 0, 0);
        };

        var execution = harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        await writer.RowObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(callbackReturned.Task.IsCompleted);
        Assert.False(execution.IsCompleted);
        writer.Release.SetResult(true);
        await execution;

        Assert.True(callbackReturned.Task.IsCompletedSuccessfully);
        Assert.Equal(EtlRunStatus.Completed, harness.Runs.TerminalStatus);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringReportConsumptionMarksRunInterrupted()
    {
        var writer = new BlockingErrorReportWriter();
        var harness = Harness(writer);
        harness.Pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
        harness.Orchestrator.Execute = async (_, reportInvalid, _, _, token) =>
        {
            await reportInvalid(InvalidResult(), token);
            throw new InvalidOperationException("The reporting callback unexpectedly returned.");
        };
        using var cancellation = new CancellationTokenSource();
        var execution = harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            cancellation.Token);

        await writer.RowObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(EtlRunStatus.Interrupted, harness.Runs.TerminalStatus);
        Assert.Equal("ETL execution was interrupted.", harness.Runs.SystemError);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationWaitsForReportWriterShutdownBeforeSourceCleanup()
    {
        var writer = new ShutdownGatedErrorReportWriter();
        var harness = Harness(writer);
        harness.Pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
        harness.Orchestrator.Execute = async (_, reportInvalid, _, _, token) =>
        {
            await reportInvalid(InvalidResult(), token);
            throw new InvalidOperationException("The reporting callback unexpectedly returned.");
        };
        using var cancellation = new CancellationTokenSource();
        var execution = harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            cancellation.Token);

        await writer.RowObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await writer.ShutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();

        Assert.False(execution.IsCompleted);
        Assert.Equal(0, harness.SourceFiles.DeleteCount);
        Assert.Equal(0, harness.Output.AbortCount);

        writer.ReleaseShutdown.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await writer.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(EtlRunStatus.Interrupted, harness.Runs.TerminalStatus);
        Assert.Null(harness.Run.ErrorReportPath);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
        Assert.Equal(1, harness.Output.AbortCount);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationPreservesInterruptedStatusWhenWriterShutdownFails()
    {
        var writer = new ShutdownGatedErrorReportWriter(new IOException("Writer shutdown failed."));
        var harness = Harness(writer);
        harness.Pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
        harness.Orchestrator.Execute = async (_, reportInvalid, _, _, token) =>
        {
            await reportInvalid(InvalidResult(), token);
            throw new InvalidOperationException("The reporting callback unexpectedly returned.");
        };
        using var cancellation = new CancellationTokenSource();
        var execution = harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            cancellation.Token);

        await writer.RowObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await writer.ShutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writer.ReleaseShutdown.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await writer.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(EtlRunStatus.Interrupted, harness.Runs.TerminalStatus);
        Assert.Null(harness.Run.ErrorReportPath);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
        Assert.Equal(1, harness.Output.AbortCount);
    }

    private static TestHarness Harness(IErrorReportWriter? errorReportWriter = null)
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
        var output = new RecordingOutputFactory();
        var sourceFiles = new MemorySourceFactory();
        var executor = new EtlRunBackgroundJobExecutor(
            runs,
            pipelines,
            orchestrator,
            loader,
            new FixedTimeProvider(),
            sourceFiles,
            errorReportWriter ?? new CsvErrorReportWriter(),
            output,
            NullLogger<EtlRunBackgroundJobExecutor>.Instance);
        return new TestHarness(run, pipeline, runs, orchestrator, loader, output, sourceFiles, executor);
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

    private static RowProcessingResult InvalidResult()
    {
        var original = new DataRow { SourceRowNumber = 3 };
        original.Values.Add("Name", "Ada");
        original.Values.Add("Email", "invalid");
        var processed = new DataRow { SourceRowNumber = 3 };
        return RowProcessingResult.Invalid(
            original,
            processed,
            [
                new RowProcessingError(
                    RowProcessingErrorStage.Validation,
                    "Name",
                    "Name is required."),
                new RowProcessingError(
                    RowProcessingErrorStage.Validation,
                    "Email",
                    "Email is invalid.")
            ]);
    }

    private sealed record TestHarness(
        EtlRun Run,
        PipelineDefinition Pipeline,
        StubRunRepository Runs,
        StubOrchestrator Orchestrator,
        StubLoader Loader,
        RecordingOutputFactory Output,
        MemorySourceFactory SourceFiles,
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

        public Task<IReadOnlyList<EtlRun>> ListByPipelineIdAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EtlRun>>([]);

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
            string? errorReportPath,
            CancellationToken cancellationToken)
        {
            TerminalStatus = status;
            TerminalProgress = finalProgress;
            SystemError = systemError;
            run.Status = status;
            run.ErrorReportPath = errorReportPath;
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
            Func<RowProcessingResult, CancellationToken, Task>,
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
            Execute(
                processBatchAsync,
                static (_, _) => Task.CompletedTask,
                reportProgressAsync,
                source,
                cancellationToken);

        public Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
            Stream source,
            PipelineDefinition pipeline,
            Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> processBatchAsync,
            Func<RowProcessingResult, CancellationToken, Task> reportInvalidRowAsync,
            Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
            CancellationToken cancellationToken) =>
            Execute(
                processBatchAsync,
                reportInvalidRowAsync,
                reportProgressAsync,
                source,
                cancellationToken);

        private static async Task<BatchExecutionResult> DefaultExecute(
            Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> load,
            Func<RowProcessingResult, CancellationToken, Task> reportInvalid,
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

    private sealed class MemorySourceFactory : IRunSourceFileStore
    {
        public int DeleteCount { get; private set; }

        public bool WasDisposedBeforeDelete { get; private set; }

        public bool ThrowOnDelete { get; set; }

        private MemoryStream? LastStream { get; set; }

        public Stream Open(EtlRun run) => LastStream = new MemoryStream([1]);

        public Task DeleteAsync(EtlRun run, CancellationToken cancellationToken)
        {
            DeleteCount++;
            WasDisposedBeforeDelete = LastStream is not null && !LastStream.CanRead;
            if (ThrowOnDelete)
            {
                throw new IOException("Simulated source cleanup failure.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutputFactory : IErrorReportStore
    {
        public int OpenCount { get; private set; }

        public int AbortCount { get; private set; }

        public MemoryStream? LastStream { get; private set; }

        public IErrorReportOutput CreateOutput(EtlRun run)
        {
            OpenCount++;
            LastStream = new MemoryStream();
            return new MemoryErrorReportOutput(this, LastStream, run.Id);
        }

        public Stream? OpenRead(EtlRun run) => null;

        public Task DeletePublishedAsync(EtlRun run, string reportReference, CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class MemoryErrorReportOutput(
            RecordingOutputFactory owner,
            MemoryStream stream,
            Guid runId) : IErrorReportOutput
        {
            public Stream Stream => stream;

            public Task<string> PublishAsync(CancellationToken cancellationToken) =>
                Task.FromResult($"error-report-{runId:N}.csv");

            public Task AbortAsync()
            {
                owner.AbortCount++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FailingErrorReportWriter : IErrorReportWriter
    {
        public Task WriteAsync(
            Stream output,
            Guid runId,
            IReadOnlyList<string> sourceFields,
            IAsyncEnumerable<RowProcessingResult> rowResults,
            CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Simulated error-report failure."));
    }

    private sealed class BlockingErrorReportWriter : IErrorReportWriter
    {
        public TaskCompletionSource<bool> RowObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WriteAsync(
            Stream output,
            Guid runId,
            IReadOnlyList<string> sourceFields,
            IAsyncEnumerable<RowProcessingResult> rowResults,
            CancellationToken cancellationToken)
        {
            await foreach (var _ in rowResults.WithCancellation(cancellationToken))
            {
                RowObserved.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class ShutdownGatedErrorReportWriter(Exception? shutdownException = null) : IErrorReportWriter
    {
        public TaskCompletionSource<bool> RowObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ShutdownStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseShutdown { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Terminated { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WriteAsync(
            Stream output,
            Guid runId,
            IReadOnlyList<string> sourceFields,
            IAsyncEnumerable<RowProcessingResult> rowResults,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var _ in rowResults.WithCancellation(cancellationToken))
                {
                    RowObserved.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        ShutdownStarted.TrySetResult(true);
                        await ReleaseShutdown.Task.ConfigureAwait(false);
                        if (shutdownException is not null)
                        {
                            throw shutdownException;
                        }

                        throw;
                    }
                }
            }
            finally
            {
                Terminated.TrySetResult(true);
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    }
}
