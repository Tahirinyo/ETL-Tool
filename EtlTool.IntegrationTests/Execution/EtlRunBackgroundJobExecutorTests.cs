using System.Runtime.CompilerServices;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Processing;
using EtlTool.Application.Reporting;
using EtlTool.Application.Sources;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
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
    public async Task ExecuteAsync_UsesAdmittedSnapshotAfterReusablePipelineIsEdited()
    {
        var reusablePipeline = new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions
            {
                CultureName = "tr-TR",
                Delimiter = CsvDelimiter.Semicolon,
                FirstRowIsHeader = true
            },
            ExpectedSchema = [new SourceFieldDefinition { Name = "CustomerId" }],
            FieldMappings =
            [
                new FieldMapping { SourceField = "CustomerId", TargetField = "customer_id" }
            ],
            TransformationRules =
            [
                new TransformationRule
                {
                    Type = TransformationType.Trim,
                    Order = 1,
                    SourceField = "customer_id"
                }
            ],
            DestinationDatabase = "admitted_database",
            DestinationCollection = "admitted_collection",
            UpsertKeyField = "customer_id"
        };
        var runRepository = new AdmissionRunRepository();
        var sourceStore = new AdmissionSourceStore(reusablePipeline.Id);
        var readiness = new ReadyReadinessService();
        var queue = new AdmissionQueue();
        var admission = new RunAdmissionService(
            new SinglePipelineService(reusablePipeline),
            readiness,
            new PipelineSourceCommitCoordinator(sourceStore),
            runRepository,
            queue,
            new RunAdmissionOptions(),
            TimeProvider.System);

        var admitted = await admission.AdmitAsync(reusablePipeline.Id, CancellationToken.None);
        var run = Assert.IsType<EtlRun>(runRepository.Run);
        Assert.Equal(RunAdmissionStatus.Admitted, admitted.Status);
        Assert.Equal(run.Id, Assert.Single(queue.Jobs).RunId);
        Assert.Same(reusablePipeline, readiness.LastEvaluatedPipeline);
        Assert.Equal(SourceType.Csv, sourceStore.LastSourceType);
        Assert.Equal("tr-TR", sourceStore.LastSourceOptions!.CultureName);

        var orchestrator = new StubOrchestrator();
        var loader = new StubLoader();
        var sourceFiles = new MemorySourceFactory();
        var output = new RecordingOutputFactory();
        var executor = new EtlRunBackgroundJobExecutor(
            runRepository,
            orchestrator,
            new DataLoaderResolver([loader]),
            new FixedTimeProvider(),
            sourceFiles,
            new CsvErrorReportWriter(),
            output,
            NullLogger<EtlRunBackgroundJobExecutor>.Instance);

        reusablePipeline.SourceOptions.CultureName = "en-US";
        reusablePipeline.ExpectedSchema[0].Name = "EditedId";
        reusablePipeline.FieldMappings[0].TargetField = "edited_id";
        reusablePipeline.TransformationRules[0].SourceField = "edited_id";
        reusablePipeline.DestinationDatabase = "edited_database";
        reusablePipeline.DestinationCollection = "edited_collection";
        reusablePipeline.UpsertKeyField = "edited_id";
        reusablePipeline.DestinationType = DestinationType.PostgreSql;

        await executor.ExecuteAsync(
            Assert.Single(queue.Jobs),
            CancellationToken.None);

        var executed = Assert.IsType<PipelineDefinition>(orchestrator.LastPipeline);
        Assert.Equal("tr-TR", executed.SourceOptions.CultureName);
        Assert.Equal("CustomerId", Assert.Single(executed.ExpectedSchema).Name);
        Assert.Equal("customer_id", Assert.Single(executed.FieldMappings).TargetField);
        Assert.Equal("customer_id", Assert.Single(executed.TransformationRules).SourceField);
        Assert.Equal("admitted_database", executed.DestinationDatabase);
        Assert.Equal("admitted_collection", executed.DestinationCollection);
        Assert.Equal("customer_id", executed.UpsertKeyField);
        Assert.Equal(DestinationType.MongoDb, executed.DestinationType);
        Assert.Equal("admitted_database", loader.LastPipeline!.DestinationDatabase);
        Assert.Equal("admitted_collection", loader.LastPipeline.DestinationCollection);
        Assert.Equal("customer_id", loader.LastPipeline.UpsertKeyField);
    }

    [Fact]
    public async Task ExecuteAsync_MissingAdmittedSnapshotFailsSafelyWithoutLoadingData()
    {
        var harness = Harness(includeExecutionConfiguration: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                CancellationToken.None));

        Assert.Equal("The admitted ETL execution configuration is unavailable.", exception.Message);
        Assert.Equal(EtlRunStatus.Failed, harness.Runs.TerminalStatus);
        Assert.Equal(
            "The admitted ETL execution configuration is unavailable.",
            harness.Runs.SystemError);
        Assert.Equal(0, harness.Loader.CallCount);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_PostgreSqlSchemaDriftFailsSafelyWithoutRowsLoadsOrErrorReport()
    {
        var harness = Harness();
        harness.SourceFiles.OpenException = new PostgreSqlSourceSchemaChangedException();

        var exception = await Assert.ThrowsAsync<PostgreSqlSourceSchemaChangedException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                CancellationToken.None));

        Assert.Equal(PostgreSqlSourceSchemaChangedException.SafeMessage, exception.Message);
        Assert.Equal(EtlRunStatus.Failed, harness.Run.Status);
        Assert.Equal(PostgreSqlSourceSchemaChangedException.SafeMessage, harness.Run.SystemError);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L), Counters(harness.Run));
        Assert.Equal(0, harness.Run.TotalRows);
        Assert.Null(harness.Run.ErrorReportPath);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.Loader.CallCount);
        Assert.Equal(0, harness.Output.OpenCount);
    }

    [Fact]
    public async Task ExecuteAsync_MongoDbSchemaDriftFailsSafelyWithoutRowsLoadsOrErrorReport()
    {
        var harness = Harness();
        harness.SourceFiles.OpenException = new MongoSourceSchemaChangedException();

        var exception = await Assert.ThrowsAsync<MongoSourceSchemaChangedException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                CancellationToken.None));

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
        Assert.Equal(EtlRunStatus.Failed, harness.Run.Status);
        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, harness.Run.SystemError);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L), Counters(harness.Run));
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.Loader.CallCount);
        Assert.Equal(0, harness.Output.OpenCount);
    }

    [Fact]
    public async Task ExecuteAsync_RunningLegacyRunWithoutSnapshotFailsAndOwnsCleanup()
    {
        var harness = Harness(includeExecutionConfiguration: false);
        var startedAt = new DateTimeOffset(2026, 8, 26, 11, 0, 0, TimeSpan.Zero);
        harness.Run.Status = EtlRunStatus.Running;
        harness.Run.StartedAt = startedAt;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                CancellationToken.None));

        Assert.Equal("The admitted ETL execution configuration is unavailable.", exception.Message);
        Assert.Equal(1, harness.Runs.StartAttempts);
        Assert.Equal(1, harness.Runs.RecoveryAttempts);
        Assert.Equal(1, harness.Runs.SuccessfulRecoveryCount);
        Assert.Equal(EtlRunStatus.Failed, harness.Run.Status);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero), harness.Run.CompletedAt);
        Assert.Equal(startedAt, harness.Run.StartedAt);
        Assert.Equal(
            "The admitted ETL execution configuration is unavailable.",
            harness.Run.SystemError);
        Assert.Null(harness.Run.ErrorReportPath);
        Assert.Equal(0, harness.Run.ProcessedRows);
        Assert.Equal(0, harness.Run.InsertedRows);
        Assert.Equal(0, harness.Run.UpdatedRows);
        Assert.Equal(0, harness.Loader.CallCount);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.SourceFiles.OpenCount);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationAfterRejectedLegacyClaimStillFailsAndCleansRun()
    {
        var harness = Harness(includeExecutionConfiguration: false);
        harness.Run.Status = EtlRunStatus.Running;
        using var cancellation = new CancellationTokenSource();
        harness.Runs.AfterRejectedStart = cancellation.Cancel;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                cancellation.Token));

        Assert.Equal("The admitted ETL execution configuration is unavailable.", exception.Message);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(harness.Runs.LastRecoveryToken.CanBeCanceled);
        Assert.Equal(EtlRunStatus.Failed, harness.Run.Status);
        Assert.Equal(1, harness.Runs.SuccessfulRecoveryCount);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.SourceFiles.OpenCount);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_RunningLegacyRecoverySourceCleanupFailurePreservesFailedState()
    {
        var harness = Harness(includeExecutionConfiguration: false);
        harness.Run.Status = EtlRunStatus.Running;
        harness.SourceFiles.ThrowOnDelete = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.ExecuteAsync(
                new BackgroundJob(harness.Run.Id),
                CancellationToken.None));

        Assert.Equal("The admitted ETL execution configuration is unavailable.", exception.Message);
        Assert.Equal(EtlRunStatus.Failed, harness.Run.Status);
        Assert.Equal(1, harness.Runs.SuccessfulRecoveryCount);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.Loader.CallCount);
        Assert.Equal(0, harness.SourceFiles.OpenCount);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateRunningLegacyRecoveryHasOneTransitionAndCleanupOwner()
    {
        var harness = Harness(includeExecutionConfiguration: false);
        harness.Run.Status = EtlRunStatus.Running;
        harness.Runs.CoordinateRejectedStarts = true;
        var job = new BackgroundJob(harness.Run.Id);

        var first = Record.ExceptionAsync(() =>
            harness.Executor.ExecuteAsync(job, CancellationToken.None));
        var second = Record.ExceptionAsync(() =>
            harness.Executor.ExecuteAsync(job, CancellationToken.None));
        var failures = await Task.WhenAll(first, second);

        var failure = Assert.Single(failures, exception => exception is not null);
        Assert.Equal(
            "The admitted ETL execution configuration is unavailable.",
            Assert.IsType<InvalidOperationException>(failure).Message);
        Assert.Equal(2, harness.Runs.StartAttempts);
        Assert.Equal(2, harness.Runs.RecoveryAttempts);
        Assert.Equal(1, harness.Runs.SuccessfulRecoveryCount);
        Assert.Equal(EtlRunStatus.Failed, harness.Run.Status);
        Assert.Equal(0, harness.Loader.CallCount);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.SourceFiles.OpenCount);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_RunningRunWithSnapshotRetainsRejectedClaimBehavior()
    {
        var harness = Harness();
        harness.Run.Status = EtlRunStatus.Running;

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(EtlRunStatus.Running, harness.Run.Status);
        Assert.Equal(1, harness.Runs.StartAttempts);
        Assert.Equal(0, harness.Runs.RecoveryAttempts);
        Assert.Equal(0, harness.Loader.CallCount);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.SourceFiles.OpenCount);
        Assert.Equal(0, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_QueuedLegacyRejectedClaimDoesNotStealRecoveryOwnership()
    {
        var harness = Harness(includeExecutionConfiguration: false);
        harness.Runs.RejectStartAsClaimedByOther = true;

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(EtlRunStatus.Running, harness.Run.Status);
        Assert.Equal(1, harness.Runs.StartAttempts);
        Assert.Equal(0, harness.Runs.RecoveryAttempts);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.SourceFiles.OpenCount);
        Assert.Equal(0, harness.SourceFiles.DeleteCount);
    }

    [Theory]
    [InlineData(EtlRunStatus.Completed)]
    [InlineData(EtlRunStatus.PartiallyCompleted)]
    [InlineData(EtlRunStatus.Failed)]
    [InlineData(EtlRunStatus.Interrupted)]
    public async Task ExecuteAsync_TerminalLegacyRunRemainsUntouched(EtlRunStatus status)
    {
        var harness = Harness(includeExecutionConfiguration: false);
        var completedAt = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        harness.Run.Status = status;
        harness.Run.CompletedAt = completedAt;
        harness.Run.SystemError = "Historical diagnostic.";

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(status, harness.Run.Status);
        Assert.Equal(completedAt, harness.Run.CompletedAt);
        Assert.Equal("Historical diagnostic.", harness.Run.SystemError);
        Assert.Equal(1, harness.Runs.StartAttempts);
        Assert.Equal(0, harness.Runs.RecoveryAttempts);
        Assert.Equal(0, harness.Runs.SuccessfulRecoveryCount);
        Assert.Equal(0, harness.Orchestrator.ExecutionCount);
        Assert.Equal(0, harness.SourceFiles.OpenCount);
        Assert.Equal(0, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessPersistsProgressAndCompletedStatus()
    {
        var harness = Harness();
        harness.Loader.Result = new BatchLoadResult(1, 0);

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(EtlRunStatus.Completed, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.Run.TotalRows);
        Assert.Equal((1L, 1L, 0L, 0L, 0L, 1L, 0L), Counters(harness.Run));
        Assert.Equal(1, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal(0, harness.Runs.TerminalProgress.UpdatedRows);
        Assert.Null(harness.Runs.SystemError);
        Assert.Equal(1, harness.Runs.ProgressUpdates);
        Assert.Equal(0, harness.Output.OpenCount);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
        Assert.True(harness.SourceFiles.WasDisposedBeforeDelete);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCompletedRunPersistsTruthfulZeroTotalAndCounters()
    {
        var harness = Harness();
        harness.Orchestrator.Execute = async (_, _, reportProgress, _, token) =>
        {
            var progress = new BatchExecutionProgress(0, 0, 0, 0, 0, isCompleted: true);
            await reportProgress(progress, token);
            return new BatchExecutionResult(0, 0, 0, 0, 0);
        };

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(EtlRunStatus.Completed, harness.Run.Status);
        Assert.Equal(0, harness.Run.TotalRows);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L), Counters(harness.Run));
    }

    [Fact]
    public async Task ExecuteAsync_CompletedRunPersistsExactTotalWithoutChangingOutcomeCounters()
    {
        var harness = Harness();
        var finalProgress = new BatchExecutionProgress(
            processedRows: 10,
            validRows: 4,
            invalidRows: 2,
            filteredRows: 1,
            deduplicatedRows: 3,
            isCompleted: true,
            insertedRows: 2,
            updatedRows: 2);
        harness.Orchestrator.Execute = async (_, _, reportProgress, _, token) =>
        {
            await reportProgress(finalProgress, token);
            return new BatchExecutionResult(10, 4, 2, 1, 3, 2, 2);
        };

        await harness.Executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None);

        Assert.Equal(10, harness.Run.TotalRows);
        Assert.Equal((10L, 4L, 2L, 1L, 3L, 2L, 2L), Counters(harness.Run));
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
        var partialProgress = new BatchExecutionProgress(
            processedRows: 8,
            validRows: 3,
            invalidRows: 2,
            filteredRows: 1,
            deduplicatedRows: 2,
            isCompleted: false,
            insertedRows: 1,
            updatedRows: 0);
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
        Assert.Equal(8, harness.Run.TotalRows);
        Assert.Equal((8L, 3L, 2L, 1L, 2L, 1L, 0L), Counters(harness.Run));
        Assert.Equal(1, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal(0, harness.Runs.ProgressUpdates);
        Assert.Equal("Destination batch loading failed.", harness.Runs.SystemError);
    }

    [Fact]
    public async Task ExecuteAsync_SourceFailurePersistsUnpublishedCountersAndCommittedWrites()
    {
        var pipeline = new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions
            {
                CultureName = "en-US",
                Delimiter = CsvDelimiter.Comma,
                FirstRowIsHeader = true
            },
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id" },
                new SourceFieldDefinition { Name = "Kind" }
            ],
            FieldMappings =
            [
                new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
                new FieldMapping { SourceField = "Kind", TargetField = "kind", IsIncluded = true }
            ],
            TransformationRules =
            [
                new TransformationRule
                {
                    Id = Guid.NewGuid(),
                    Type = TransformationType.FilterRow,
                    Order = 1,
                    SourceField = "kind",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Operator"] = FilterOperator.Equals.ToString(),
                        ["Value"] = "skip"
                    }
                }
            ],
            DestinationDatabase = "demo",
            DestinationCollection = "rows",
            UpsertKeyField = "id"
        };
        var harness = Harness(pipeline: pipeline);
        harness.Loader.Result = new BatchLoadResult(3, 0);
        harness.SourceFiles.Rows =
        [
            SourceRow(2, "A", "keep"),
            SourceRow(3, "B", "keep"),
            SourceRow(4, "C", "keep"),
            SourceRow(5, null, "keep"),
            SourceRow(6, "D", "skip"),
            SourceRow(7, "A", "keep")
        ];
        harness.SourceFiles.TerminalFailure = new IOException("The source cursor failed.");
        var orchestrator = new BatchOrchestrator(
            new ReadyReadinessService(),
            new PipelineRowProcessor(
                new FieldMappingService(),
                new TransformationEngine(new TransformationHandlerRegistry(
                    [new ConditionalFilterTransformationHandler()])),
                new ValidationEngine(new ValidationHandlerRegistry([]))),
            new BatchExecutionOptions { BatchSize = 3 });
        var executor = new EtlRunBackgroundJobExecutor(
            harness.Runs,
            orchestrator,
            new DataLoaderResolver([harness.Loader]),
            new FixedTimeProvider(),
            harness.SourceFiles,
            new CsvErrorReportWriter(),
            harness.Output,
            NullLogger<EtlRunBackgroundJobExecutor>.Instance);

        await Assert.ThrowsAsync<BatchExecutionException>(() => executor.ExecuteAsync(
            new BackgroundJob(harness.Run.Id),
            CancellationToken.None));

        Assert.Equal(EtlRunStatus.PartiallyCompleted, harness.Runs.TerminalStatus);
        Assert.Equal((6L, 3L, 1L, 1L, 1L, 3L, 0L), Counters(harness.Run));
        Assert.Equal(3, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal(0, harness.Runs.TerminalProgress.UpdatedRows);
        Assert.Equal("The ETL source file could not be read.", harness.Runs.SystemError);
        Assert.Equal($"error-report-{harness.Run.Id:N}.csv", harness.Run.ErrorReportPath);
        var csv = System.Text.Encoding.UTF8.GetString(harness.Output.LastStream!.ToArray());
        Assert.Contains("Upsert key field 'id' is required.", csv);
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
        Assert.Equal(1, harness.Run.TotalRows);
        Assert.Equal(1, harness.Runs.TerminalProgress!.InsertedRows);
        Assert.Equal("ETL execution was interrupted.", harness.Runs.SystemError);
        Assert.Equal(1, harness.SourceFiles.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_StreamsInvalidFullRunResultsThroughCsvWriter()
    {
        var harness = Harness();
        harness.ExecutionConfiguration.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Name" },
            new SourceFieldDefinition { Name = "Email" }
        ];
        harness.ExecutionConfiguration.UpsertKeyField = "customer_id";
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
        Assert.Contains("RunId,SourceRowNumber,ErrorStage,ErrorField,RuleId,TransformationType,ErrorMessage,EffectiveUpsertKeyValue,Source:Name,Source:Email", csv);
        Assert.Contains("Name is required.", csv);
        Assert.Contains("Email is invalid.", csv);
        Assert.Contains("Ada", csv);
        Assert.Contains("Source:Name", csv);
        Assert.Contains("Source:Email", csv);
        Assert.Contains("customer-42", csv);
        Assert.Equal(EtlRunStatus.Completed, harness.Runs.TerminalStatus);
        Assert.Equal(1, harness.Runs.TerminalProgress!.InvalidRows);
        Assert.Equal($"error-report-{harness.Run.Id:N}.csv", harness.Run.ErrorReportPath);
    }

    [Fact]
    public async Task ExecuteAsync_FinalizesHealthyInvalidRowReportBeforeLaterSystemFailure()
    {
        var harness = Harness();
        harness.ExecutionConfiguration.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
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
        harness.ExecutionConfiguration.ExpectedSchema =
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
        harness.ExecutionConfiguration.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
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
        harness.ExecutionConfiguration.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
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
        harness.ExecutionConfiguration.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
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
        harness.ExecutionConfiguration.ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }];
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

    private static TestHarness Harness(
        IErrorReportWriter? errorReportWriter = null,
        PipelineDefinition? pipeline = null,
        bool includeExecutionConfiguration = true)
    {
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            Status = EtlRunStatus.Queued,
            StoredFilePath = "generated.upload"
        };
        pipeline ??= new PipelineDefinition { Id = run.PipelineId };
        run.PipelineId = pipeline.Id;
        run.ExecutionConfiguration = includeExecutionConfiguration
            ? EtlRunExecutionConfiguration.Capture(pipeline)
            : null;
        var runs = new StubRunRepository(run);
        var orchestrator = new StubOrchestrator();
        var loader = new StubLoader();
        var output = new RecordingOutputFactory();
        var sourceFiles = new MemorySourceFactory();
        var executor = new EtlRunBackgroundJobExecutor(
            runs,
            orchestrator,
            new DataLoaderResolver([loader]),
            new FixedTimeProvider(),
            sourceFiles,
            errorReportWriter ?? new CsvErrorReportWriter(),
            output,
            NullLogger<EtlRunBackgroundJobExecutor>.Instance);
        return new TestHarness(
            run,
            run.ExecutionConfiguration ?? new EtlRunExecutionConfiguration(),
            runs,
            orchestrator,
            loader,
            output,
            sourceFiles,
            executor);
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

    private static DataRow SourceRow(long sourceRowNumber, string? id, string kind)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };
        row.Values.Add("Id", id);
        row.Values.Add("Kind", kind);
        return row;
    }

    private static (long, long, long, long, long, long, long) Counters(EtlRun run) =>
        (run.ProcessedRows, run.ValidRows, run.InvalidRows, run.FilteredRows,
            run.DeduplicatedRows, run.InsertedRows, run.UpdatedRows);

    private static RowProcessingResult InvalidResult()
    {
        var original = new DataRow { SourceRowNumber = 3 };
        original.Values.Add("Name", "Ada");
        original.Values.Add("Email", "invalid");
        var processed = new DataRow { SourceRowNumber = 3 };
        processed.Values.Add("customer_id", "customer-42");
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
        EtlRunExecutionConfiguration ExecutionConfiguration,
        StubRunRepository Runs,
        StubOrchestrator Orchestrator,
        StubLoader Loader,
        RecordingOutputFactory Output,
        MemorySourceFactory SourceFiles,
        EtlRunBackgroundJobExecutor Executor);

    private sealed class StubRunRepository(EtlRun run) : IEtlRunRepository
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _rejectedStartsReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rejectedStartCount;

        public bool AcceptProgress { get; set; } = true;

        public bool CoordinateRejectedStarts { get; set; }

        public bool RejectStartAsClaimedByOther { get; set; }

        public Action? AfterRejectedStart { get; set; }

        public int StartAttempts { get; private set; }

        public int RecoveryAttempts { get; private set; }

        public int SuccessfulRecoveryCount { get; private set; }

        public CancellationToken LastRecoveryToken { get; private set; }

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

        public Task<IReadOnlyList<EtlRun>> ListNonTerminalAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EtlRun>>([]);

        public async Task<bool> TryStartAsync(
            Guid runId,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken)
        {
            bool started;
            lock (_sync)
            {
                StartAttempts++;
                started = run.Id == runId && run.Status == EtlRunStatus.Queued;
                if (started && RejectStartAsClaimedByOther)
                {
                    run.Status = EtlRunStatus.Running;
                    run.StartedAt = startedAt;
                    started = false;
                }
                else if (started)
                {
                    run.Status = EtlRunStatus.Running;
                    run.StartedAt = startedAt;
                }
            }

            if (!started && CoordinateRejectedStarts)
            {
                if (Interlocked.Increment(ref _rejectedStartCount) == 2)
                {
                    _rejectedStartsReleased.TrySetResult();
                }

                await _rejectedStartsReleased.Task.WaitAsync(cancellationToken);
            }

            if (!started)
            {
                AfterRejectedStart?.Invoke();
            }

            return started;
        }

        public Task<bool> TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
            Guid runId,
            DateTimeOffset completedAt,
            string systemError,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                RecoveryAttempts++;
                LastRecoveryToken = cancellationToken;
                if (run.Id != runId
                    || run.Status != EtlRunStatus.Running
                    || run.ExecutionConfiguration is not null)
                {
                    return Task.FromResult(false);
                }

                run.Status = EtlRunStatus.Failed;
                run.CompletedAt = completedAt;
                run.SystemError = systemError;
                run.ErrorReportPath = null;
                TerminalStatus = EtlRunStatus.Failed;
                SystemError = systemError;
                SuccessfulRecoveryCount++;
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryInterruptAsync(
            Guid runId,
            EtlRunStatus expectedStatus,
            DateTimeOffset completedAt,
            long observedRows,
            string systemError,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryUpdateProgressAsync(
            Guid runId,
            BatchExecutionProgress progress,
            CancellationToken cancellationToken)
        {
            ProgressUpdates++;
            if (AcceptProgress)
            {
                ApplyProgress(progress, completed: false);
            }

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
            lock (_sync)
            {
                var canTransition = run.Id == runId
                    && (status == EtlRunStatus.Interrupted
                        ? run.Status is EtlRunStatus.Queued or EtlRunStatus.Running
                        : run.Status == EtlRunStatus.Running);
                if (!canTransition)
                {
                    return Task.FromResult(false);
                }

                TerminalStatus = status;
                TerminalProgress = finalProgress;
                SystemError = systemError;
                run.Status = status;
                run.CompletedAt = completedAt;
                run.SystemError = systemError;
                run.ErrorReportPath = errorReportPath;
                if (finalProgress is not null)
                {
                    ApplyProgress(finalProgress, status == EtlRunStatus.Completed);
                }
                return Task.FromResult(true);
            }
        }

        private void ApplyProgress(BatchExecutionProgress progress, bool completed)
        {
            run.ProcessedRows = progress.ProcessedRows;
            run.ValidRows = progress.ValidRows;
            run.InvalidRows = progress.InvalidRows;
            run.FilteredRows = progress.FilteredRows;
            run.DeduplicatedRows = progress.DeduplicatedRows;
            run.InsertedRows = progress.InsertedRows;
            run.UpdatedRows = progress.UpdatedRows;
            run.TotalRows = completed
                ? progress.ProcessedRows
                : Math.Max(run.TotalRows, progress.ProcessedRows);
        }
    }

    private sealed class AdmissionRunRepository : IEtlRunRepository
    {
        public EtlRun? Run { get; private set; }

        public Task AddAsync(EtlRun run, CancellationToken cancellationToken)
        {
            Run = run;
            return Task.CompletedTask;
        }

        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(Run?.Id == runId ? Run : null);

        public Task<IReadOnlyList<EtlRun>> ListByPipelineIdAsync(
            Guid pipelineId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EtlRun>>([]);

        public Task<IReadOnlyList<EtlRun>> ListNonTerminalAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EtlRun>>([]);

        public Task<bool> TryStartAsync(
            Guid runId,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken)
        {
            if (Run?.Id != runId || Run.Status != EtlRunStatus.Queued)
            {
                return Task.FromResult(false);
            }

            Run.Status = EtlRunStatus.Running;
            Run.StartedAt = startedAt;
            return Task.FromResult(true);
        }

        public Task<bool> TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
            Guid runId,
            DateTimeOffset completedAt,
            string systemError,
            CancellationToken cancellationToken)
        {
            if (Run?.Id != runId
                || Run.Status != EtlRunStatus.Running
                || Run.ExecutionConfiguration is not null)
            {
                return Task.FromResult(false);
            }

            Run.Status = EtlRunStatus.Failed;
            Run.CompletedAt = completedAt;
            Run.SystemError = systemError;
            Run.ErrorReportPath = null;
            return Task.FromResult(true);
        }

        public Task<bool> TryInterruptAsync(
            Guid runId,
            EtlRunStatus expectedStatus,
            DateTimeOffset completedAt,
            long observedRows,
            string systemError,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryUpdateProgressAsync(
            Guid runId,
            BatchExecutionProgress progress,
            CancellationToken cancellationToken) => Task.FromResult(Run?.Id == runId);

        public Task<bool> TryMarkTerminalAsync(
            Guid runId,
            EtlRunStatus status,
            DateTimeOffset completedAt,
            BatchExecutionProgress? finalProgress,
            string? systemError,
            string? errorReportPath,
            CancellationToken cancellationToken)
        {
            if (Run?.Id != runId)
            {
                return Task.FromResult(false);
            }

            Run.Status = status;
            Run.CompletedAt = completedAt;
            Run.SystemError = systemError;
            Run.ErrorReportPath = errorReportPath;
            return Task.FromResult(true);
        }
    }

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
        public PipelineDefinition? LastEvaluatedPipeline { get; private set; }

        public PipelineReadinessResult Evaluate(PipelineDefinition pipeline)
        {
            LastEvaluatedPipeline = pipeline;
            return new PipelineReadinessResult([]);
        }

        public Task<PipelineReadinessResult?> EvaluateAsync(
            Guid pipelineId,
            CancellationToken cancellationToken) => Task.FromResult<PipelineReadinessResult?>(
                new PipelineReadinessResult([]));
    }

    private sealed class AdmissionSourceStore(Guid pipelineId) : IWizardSourceStore
    {
        public SourceType? LastSourceType { get; private set; }

        public SourceOptions? LastSourceOptions { get; private set; }

        public Task<IWizardRunSourceReservation?> ReserveForRunAsync(
            Guid requestedPipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            LastSourceType = sourceType;
            LastSourceOptions = sourceOptions;
            return Task.FromResult<IWizardRunSourceReservation?>(
                requestedPipelineId == pipelineId ? new AdmissionSourceReservation() : null);
        }

        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AdmissionSourceReservation : IWizardRunSourceReservation
    {
        public string OriginalFileName => "customers.csv";

        public string StoredFilePath => "generated.upload";

        public void TransferToRun()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdmissionQueue : IBackgroundJobQueue
    {
        public List<BackgroundJob> Jobs { get; } = [];

        public ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubOrchestrator : IBatchOrchestrator
    {
        public PipelineDefinition? LastPipeline { get; private set; }

        public int ExecutionCount { get; private set; }

        public Func<
            Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>>,
            Func<RowProcessingResult, CancellationToken, Task>,
            Func<BatchExecutionProgress, CancellationToken, Task>,
            IEtlSource,
            CancellationToken,
            Task<BatchExecutionResult>> Execute { get; set; } = DefaultExecute;

        public async Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
            IEtlSource source,
            PipelineDefinition pipeline,
            IDataLoader loader,
            Func<RowProcessingResult, CancellationToken, Task> reportInvalidRowAsync,
            Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
            CancellationToken cancellationToken)
        {
            LastPipeline = pipeline;
            ExecutionCount++;
            await loader.PrepareAsync(pipeline, cancellationToken);
            return await Execute(
                (rows, token) => loader.UpsertBatchAsync(rows, pipeline, token),
                reportInvalidRowAsync,
                reportProgressAsync,
                source,
                cancellationToken);
        }

        private static async Task<BatchExecutionResult> DefaultExecute(
            Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> load,
            Func<RowProcessingResult, CancellationToken, Task> reportInvalid,
            Func<BatchExecutionProgress, CancellationToken, Task> report,
            IEtlSource source,
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
        public DestinationType DestinationType => DestinationType.MongoDb;

        public BatchLoadResult Result { get; set; } = BatchLoadResult.Empty;

        public int CallCount { get; private set; }

        public PipelineDefinition? LastPipeline { get; private set; }

        public int PrepareCallCount { get; private set; }

        public Task PrepareAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            PrepareCallCount++;
            LastPipeline = pipeline;
            return Task.CompletedTask;
        }

        public Task<BatchLoadResult> UpsertBatchAsync(
            IReadOnlyList<DataRow> rows,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastPipeline = pipeline;
            return Task.FromResult(Result);
        }
    }

    private sealed class MemorySourceFactory : IRunSourceStore
    {
        private int _deleteCount;
        private int _openCount;

        public int DeleteCount => Volatile.Read(ref _deleteCount);

        public int OpenCount => Volatile.Read(ref _openCount);

        public bool WasDisposedBeforeDelete { get; private set; }

        public bool ThrowOnDelete { get; set; }

        public Exception? OpenException { get; set; }

        public IReadOnlyList<DataRow> Rows { get; set; } = [];

        public Exception? TerminalFailure { get; set; }

        private MemoryStream? LastStream { get; set; }

        public Task<IEtlSource> OpenAsync(EtlRun run, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _openCount);
            if (OpenException is not null)
            {
                return Task.FromException<IEtlSource>(OpenException);
            }

            LastStream = new MemoryStream([1]);
            return Task.FromResult<IEtlSource>(new MemoryEtlSource(
                LastStream,
                Rows,
                TerminalFailure));
        }

        public Task ReleaseAsync(EtlRun run, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _deleteCount);
            WasDisposedBeforeDelete = LastStream is not null && !LastStream.CanRead;
            if (ThrowOnDelete)
            {
                throw new IOException("Simulated source cleanup failure.");
            }
            return Task.CompletedTask;
        }

        private sealed class MemoryEtlSource(
            MemoryStream stream,
            IReadOnlyList<DataRow> rows,
            Exception? terminalFailure) : IEtlSource
        {
            public async IAsyncEnumerable<DataRow> ReadAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.Yield();
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return row;
                }

                if (terminalFailure is not null)
                {
                    throw terminalFailure;
                }
            }

            public ValueTask DisposeAsync() => stream.DisposeAsync();
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
            string upsertKeyField,
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
            string upsertKeyField,
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
            string upsertKeyField,
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
