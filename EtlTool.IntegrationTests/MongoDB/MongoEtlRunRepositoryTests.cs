using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoEtlRunRepositoryTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task AddAndGetAsync_RoundTripsEveryEtlRunField()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateRun();

        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);

        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.NotNull(persisted);
        AssertRunEqual(run, persisted);
    }

    [Fact]
    public async Task GetByIdAsync_DeserializesLegacyRunWithoutExecutionConfiguration()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var runId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();
        var collection = testDatabase.Database.GetCollection<BsonDocument>(
            MongoMetadataCollectionNames.EtlRuns);
        await collection.InsertOneAsync(new BsonDocument
        {
            ["_id"] = new BsonBinaryData(runId, GuidRepresentation.Standard),
            [nameof(EtlRun.PipelineId)] = new BsonBinaryData(
                pipelineId,
                GuidRepresentation.Standard),
            [nameof(EtlRun.PipelineName)] = "Legacy pipeline",
            [nameof(EtlRun.Status)] = (int)EtlRunStatus.Completed
        });

        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            runId,
            CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(pipelineId, persisted.PipelineId);
        Assert.Equal(EtlRunStatus.Completed, persisted.Status);
        Assert.Null(persisted.ExecutionConfiguration);
    }

    [Fact]
    public async Task GetByIdAsync_LegacyExecutionConfigurationWithoutDestinationTypeDefaultsToMongoDb()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateRun();
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
        var collection = testDatabase.Database.GetCollection<BsonDocument>(
            MongoMetadataCollectionNames.EtlRuns);
        await collection.UpdateOneAsync(
            new BsonDocument(
                "_id",
                new BsonBinaryData(run.Id, GuidRepresentation.Standard)),
            new BsonDocument(
                "$unset",
                new BsonDocument(
                    $"{nameof(EtlRun.ExecutionConfiguration)}.{nameof(EtlRunExecutionConfiguration.DestinationType)}",
                    string.Empty)));

        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.NotNull(persisted?.ExecutionConfiguration);
        Assert.Equal(
            DestinationType.MongoDb,
            persisted.ExecutionConfiguration.DestinationType);
    }

    [Fact]
    public async Task ListByPipelineIdAsync_ReturnsOnlyMatchingRunsNewestFirst()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var pipelineId = Guid.NewGuid();
        var older = CreateRun();
        older.PipelineId = pipelineId;
        older.StartedAt = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
        var newer = CreateRun();
        newer.PipelineId = pipelineId;
        newer.StartedAt = new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        var unrelated = CreateRun();

        await testDatabase.EtlRunRepository.AddAsync(older, CancellationToken.None);
        await testDatabase.EtlRunRepository.AddAsync(unrelated, CancellationToken.None);
        await testDatabase.EtlRunRepository.AddAsync(newer, CancellationToken.None);

        var runs = await testDatabase.EtlRunRepository.ListByPipelineIdAsync(pipelineId, CancellationToken.None);

        Assert.Equal([newer.Id, older.Id], runs.Select(run => run.Id));
        Assert.DoesNotContain(runs, run => run.PipelineId != pipelineId);
    }

    [Fact]
    public async Task TryStartAsync_ClaimsQueuedRunOnceAndPreservesOtherFields()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateRun();
        run.Status = EtlRunStatus.Queued;
        run.StartedAt = null;
        run.CompletedAt = null;
        run.SystemError = null;
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
        var startedAt = new DateTimeOffset(2026, 8, 25, 10, 15, 0, TimeSpan.Zero);

        var claimed = await testDatabase.EtlRunRepository.TryStartAsync(
            run.Id,
            startedAt,
            CancellationToken.None);
        var claimedAgain = await testDatabase.EtlRunRepository.TryStartAsync(
            run.Id,
            startedAt.AddMinutes(1),
            CancellationToken.None);
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.True(claimed);
        Assert.False(claimedAgain);
        Assert.NotNull(persisted);
        Assert.Equal(EtlRunStatus.Running, persisted.Status);
        Assert.Equal(startedAt, persisted.StartedAt);
        Assert.Equal(run.TotalRows, persisted.TotalRows);
        Assert.Equal(run.InsertedRows, persisted.InsertedRows);
        Assert.Equal(run.UpdatedRows, persisted.UpdatedRows);
        Assert.Equal(run.ErrorReportPath, persisted.ErrorReportPath);
        AssertExecutionConfigurationEqual(run.ExecutionConfiguration, persisted.ExecutionConfiguration);
    }

    [Fact]
    public async Task TryFailLegacyRunningRunWithoutExecutionConfigurationAsync_AtomicallyRecoversAbsentSnapshotOnce()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateRun();
        run.Status = EtlRunStatus.Running;
        run.CompletedAt = null;
        run.ProcessedRows = 0;
        run.ValidRows = 0;
        run.InvalidRows = 0;
        run.FilteredRows = 0;
        run.DeduplicatedRows = 0;
        run.InsertedRows = 0;
        run.UpdatedRows = 0;
        run.SystemError = null;
        run.ErrorReportPath = "stale-report.csv";
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
        var collection = testDatabase.Database.GetCollection<BsonDocument>(
            MongoMetadataCollectionNames.EtlRuns);
        await collection.UpdateOneAsync(
            new BsonDocument(
                "_id",
                new BsonBinaryData(run.Id, GuidRepresentation.Standard)),
            new BsonDocument(
                "$unset",
                new BsonDocument(nameof(EtlRun.ExecutionConfiguration), string.Empty)));
        var completedAt = new DateTimeOffset(2026, 8, 27, 15, 0, 0, TimeSpan.Zero);
        const string systemError = "The admitted ETL execution configuration is unavailable.";

        var started = await testDatabase.EtlRunRepository.TryStartAsync(
            run.Id,
            completedAt.AddMinutes(-1),
            CancellationToken.None);
        var recoveries = await Task.WhenAll(
            testDatabase.EtlRunRepository
                .TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
                    run.Id,
                    completedAt,
                    systemError,
                    CancellationToken.None),
            testDatabase.EtlRunRepository
                .TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
                    run.Id,
                    completedAt.AddSeconds(1),
                    "A duplicate recovery must not overwrite the winner.",
                    CancellationToken.None));
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.False(started);
        Assert.Equal(1, recoveries.Count(recovered => recovered));
        Assert.NotNull(persisted);
        Assert.Equal(EtlRunStatus.Failed, persisted.Status);
        Assert.Contains(persisted.CompletedAt, new DateTimeOffset?[] { completedAt, completedAt.AddSeconds(1) });
        Assert.Contains(
            persisted.SystemError,
            new[] { systemError, "A duplicate recovery must not overwrite the winner." });
        Assert.Null(persisted.ErrorReportPath);
        Assert.Null(persisted.ExecutionConfiguration);
        Assert.Equal(run.StartedAt, persisted.StartedAt);
        Assert.Equal(0, persisted.ProcessedRows);
        Assert.Equal(0, persisted.InsertedRows);
        Assert.Equal(0, persisted.UpdatedRows);
    }

    [Fact]
    public async Task TryFailLegacyRunningRunWithoutExecutionConfigurationAsync_RejectsValidRunningRun()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateRun();
        run.Status = EtlRunStatus.Running;
        run.CompletedAt = null;
        run.SystemError = null;
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);

        var recovered = await testDatabase.EtlRunRepository
            .TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
                run.Id,
                DateTimeOffset.UtcNow,
                "The admitted ETL execution configuration is unavailable.",
                CancellationToken.None);
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.False(recovered);
        Assert.NotNull(persisted);
        Assert.Equal(EtlRunStatus.Running, persisted.Status);
        Assert.Null(persisted.CompletedAt);
        Assert.Null(persisted.SystemError);
        AssertExecutionConfigurationEqual(run.ExecutionConfiguration, persisted.ExecutionConfiguration);
    }

    [Fact]
    public async Task TryFailLegacyRunningRunWithoutExecutionConfigurationAsync_RecoversExplicitNullSnapshot()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateRun();
        run.Status = EtlRunStatus.Running;
        run.CompletedAt = null;
        run.SystemError = null;
        run.ErrorReportPath = null;
        run.ExecutionConfiguration = null;
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
        var completedAt = new DateTimeOffset(2026, 8, 27, 15, 5, 0, TimeSpan.Zero);

        var recovered = await testDatabase.EtlRunRepository
            .TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
                run.Id,
                completedAt,
                "The admitted ETL execution configuration is unavailable.",
                CancellationToken.None);
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.True(recovered);
        Assert.NotNull(persisted);
        Assert.Equal(EtlRunStatus.Failed, persisted.Status);
        Assert.Equal(completedAt, persisted.CompletedAt);
        Assert.Equal(
            "The admitted ETL execution configuration is unavailable.",
            persisted.SystemError);
        Assert.Null(persisted.ExecutionConfiguration);
    }

    [Theory]
    [InlineData(EtlRunStatus.Completed)]
    [InlineData(EtlRunStatus.PartiallyCompleted)]
    [InlineData(EtlRunStatus.Failed)]
    [InlineData(EtlRunStatus.Interrupted)]
    public async Task TryFailLegacyRunningRunWithoutExecutionConfigurationAsync_RejectsTerminalRun(
        EtlRunStatus status)
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateRun();
        run.Status = status;
        run.ExecutionConfiguration = null;
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);

        var recovered = await testDatabase.EtlRunRepository
            .TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
                run.Id,
                run.CompletedAt!.Value.AddMinutes(1),
                "The admitted ETL execution configuration is unavailable.",
                CancellationToken.None);
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.False(recovered);
        Assert.NotNull(persisted);
        Assert.Equal(status, persisted.Status);
        Assert.Equal(run.CompletedAt, persisted.CompletedAt);
        Assert.Equal(run.SystemError, persisted.SystemError);
    }

    [Fact]
    public async Task TryUpdateProgressAsync_PersistsMonotonicSnapshotsWithoutResettingUnrelatedFields()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateQueuedRun();
        run.SystemError = "Existing diagnostic information must be preserved.";
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
        var progressBeforeStart = await testDatabase.EtlRunRepository.TryUpdateProgressAsync(
            run.Id,
            Progress(1, 1, 0, 0, 0),
            CancellationToken.None);
        var started = await testDatabase.EtlRunRepository.TryStartAsync(
            run.Id,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var progress = Progress(10, 5, 2, 1, 2, insertedRows: 3, updatedRows: 2);

        var firstUpdate = await testDatabase.EtlRunRepository.TryUpdateProgressAsync(
            run.Id,
            progress,
            CancellationToken.None);
        var equalUpdate = await testDatabase.EtlRunRepository.TryUpdateProgressAsync(
            run.Id,
            progress,
            CancellationToken.None);
        var staleUpdate = await testDatabase.EtlRunRepository.TryUpdateProgressAsync(
            run.Id,
            Progress(9, 5, 2, 1, 1),
            CancellationToken.None);
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.False(progressBeforeStart);
        Assert.True(started);
        Assert.True(firstUpdate);
        Assert.True(equalUpdate);
        Assert.False(staleUpdate);
        Assert.NotNull(persisted);
        Assert.Equal(progress.ProcessedRows, persisted.ProcessedRows);
        Assert.Equal(progress.ValidRows, persisted.ValidRows);
        Assert.Equal(progress.InvalidRows, persisted.InvalidRows);
        Assert.Equal(progress.FilteredRows, persisted.FilteredRows);
        Assert.Equal(progress.DeduplicatedRows, persisted.DeduplicatedRows);
        Assert.Equal(progress.ProcessedRows, persisted.TotalRows);
        Assert.Equal(progress.InsertedRows, persisted.InsertedRows);
        Assert.Equal(progress.UpdatedRows, persisted.UpdatedRows);
        Assert.Equal(run.ErrorReportPath, persisted.ErrorReportPath);
        Assert.Equal(run.SystemError, persisted.SystemError);
        Assert.Equal(run.OriginalFileName, persisted.OriginalFileName);
        Assert.Equal(run.StoredFilePath, persisted.StoredFilePath);
        AssertExecutionConfigurationEqual(run.ExecutionConfiguration, persisted.ExecutionConfiguration);
    }

    [Fact]
    public async Task TryMarkTerminalAsync_PersistsTerminalDetailsAndPreventsFurtherUpdates()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateQueuedRun();
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
        await testDatabase.EtlRunRepository.TryStartAsync(
            run.Id,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await testDatabase.EtlRunRepository.TryUpdateProgressAsync(
            run.Id,
            Progress(4, 2, 1, 1, 0, insertedRows: 1, updatedRows: 1),
            CancellationToken.None);
        var completedAt = new DateTimeOffset(2026, 8, 25, 11, 0, 0, TimeSpan.Zero);
        var finalProgress = Progress(5, 3, 1, 1, 0, insertedRows: 2, updatedRows: 1);

        var completed = await testDatabase.EtlRunRepository.TryMarkTerminalAsync(
            run.Id,
            EtlRunStatus.PartiallyCompleted,
            completedAt,
            finalProgress,
            "Target database became unavailable.",
            "error-report-0123456789abcdef0123456789abcdef.csv",
            CancellationToken.None);
        var progressAfterTerminal = await testDatabase.EtlRunRepository.TryUpdateProgressAsync(
            run.Id,
            Progress(5, 3, 1, 1, 0),
            CancellationToken.None);
        var terminalAfterTerminal = await testDatabase.EtlRunRepository.TryMarkTerminalAsync(
            run.Id,
            EtlRunStatus.Failed,
            completedAt.AddMinutes(1),
            finalProgress: null,
            "A later failure must not replace the first.",
            errorReportPath: null,
            CancellationToken.None);
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.True(completed);
        Assert.False(progressAfterTerminal);
        Assert.False(terminalAfterTerminal);
        Assert.NotNull(persisted);
        Assert.Equal(EtlRunStatus.PartiallyCompleted, persisted.Status);
        Assert.Equal(completedAt, persisted.CompletedAt);
        Assert.Equal("Target database became unavailable.", persisted.SystemError);
        Assert.Equal("error-report-0123456789abcdef0123456789abcdef.csv", persisted.ErrorReportPath);
        Assert.Equal(5, persisted.ProcessedRows);
        Assert.Equal(5, persisted.TotalRows);
        Assert.Equal(2, persisted.InsertedRows);
        Assert.Equal(1, persisted.UpdatedRows);
        AssertExecutionConfigurationEqual(run.ExecutionConfiguration, persisted.ExecutionConfiguration);
    }

    [Fact]
    public async Task TryMarkTerminalAsync_AllowsQueuedRunToBeInterruptedWhenAbandoned()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateQueuedRun();
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);
        var completedAt = new DateTimeOffset(2026, 8, 25, 11, 5, 0, TimeSpan.Zero);

        var interrupted = await testDatabase.EtlRunRepository.TryMarkTerminalAsync(
            run.Id,
            EtlRunStatus.Interrupted,
            completedAt,
            finalProgress: null,
            "The application stopped before this run started.",
            errorReportPath: null,
            CancellationToken.None);
        var persisted = await testDatabase.EtlRunRepository.GetByIdAsync(
            run.Id,
            CancellationToken.None);

        Assert.True(interrupted);
        Assert.NotNull(persisted);
        Assert.Equal(EtlRunStatus.Interrupted, persisted.Status);
        Assert.Equal(completedAt, persisted.CompletedAt);
        Assert.Equal("The application stopped before this run started.", persisted.SystemError);
    }

    [Fact]
    public async Task NonTerminalRecovery_ClaimsExactStatusesOnceAndPreservesCountersAndTerminalRuns()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var queued = CreateQueuedRun();
        var running = CreateQueuedRun();
        running.Status = EtlRunStatus.Running;
        running.ProcessedRows = 7;
        running.ValidRows = 3;
        running.InvalidRows = 1;
        running.FilteredRows = 1;
        running.DeduplicatedRows = 2;
        running.InsertedRows = 2;
        running.UpdatedRows = 1;
        var terminalRuns = new[]
        {
            CreateRun(EtlRunStatus.Completed),
            CreateRun(EtlRunStatus.PartiallyCompleted),
            CreateRun(EtlRunStatus.Failed),
            CreateRun(EtlRunStatus.Interrupted)
        };
        await testDatabase.EtlRunRepository.AddAsync(queued, CancellationToken.None);
        await testDatabase.EtlRunRepository.AddAsync(running, CancellationToken.None);
        foreach (var terminal in terminalRuns)
        {
            await testDatabase.EtlRunRepository.AddAsync(terminal, CancellationToken.None);
        }

        var candidates = await testDatabase.EtlRunRepository
            .ListNonTerminalAsync(CancellationToken.None);
        var completedAt = new DateTimeOffset(2026, 8, 27, 16, 0, 0, TimeSpan.Zero);
        var queuedRecovered = await testDatabase.EtlRunRepository.TryInterruptAsync(
            queued.Id, EtlRunStatus.Queued, completedAt, queued.ProcessedRows,
            "Recovered queued run.", CancellationToken.None);
        var runningRecovered = await testDatabase.EtlRunRepository.TryInterruptAsync(
            running.Id, EtlRunStatus.Running, completedAt, running.ProcessedRows,
            "Recovered running run.", CancellationToken.None);
        var duplicateRecovery = await testDatabase.EtlRunRepository.TryInterruptAsync(
            running.Id, EtlRunStatus.Running, completedAt.AddMinutes(1), running.ProcessedRows,
            "Duplicate recovery.", CancellationToken.None);
        var terminalRecovery = await testDatabase.EtlRunRepository.TryInterruptAsync(
            terminalRuns[0].Id, EtlRunStatus.Queued, completedAt, 0,
            "Must not recover terminal run.", CancellationToken.None);

        Assert.Equal(
            new[] { queued.Id, running.Id }.Order(),
            candidates.Select(run => run.Id).Order());
        Assert.True(queuedRecovered);
        Assert.True(runningRecovered);
        Assert.False(duplicateRecovery);
        Assert.False(terminalRecovery);
        var persistedQueued = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository
            .GetByIdAsync(queued.Id, CancellationToken.None));
        var persistedRunning = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository
            .GetByIdAsync(running.Id, CancellationToken.None));
        Assert.Equal(EtlRunStatus.Interrupted, persistedQueued.Status);
        Assert.Equal(completedAt, persistedQueued.CompletedAt);
        Assert.Equal(0, persistedQueued.TotalRows);
        Assert.Equal(EtlRunStatus.Interrupted, persistedRunning.Status);
        Assert.Equal(completedAt, persistedRunning.CompletedAt);
        Assert.Equal(7, persistedRunning.TotalRows);
        Assert.Equal((7L, 3L, 1L, 1L, 2L, 2L, 1L), Counters(persistedRunning));
        foreach (var terminal in terminalRuns)
        {
            var persisted = Assert.IsType<EtlRun>(await testDatabase.EtlRunRepository
                .GetByIdAsync(terminal.Id, CancellationToken.None));
            Assert.Equal(terminal.Status, persisted.Status);
            Assert.Equal(terminal.CompletedAt, persisted.CompletedAt);
            Assert.Equal(terminal.SystemError, persisted.SystemError);
        }
    }

    [Fact]
    public async Task MissingRuns_ReturnNullForReadAndFalseForGuardedUpdates()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var runId = Guid.NewGuid();

        Assert.Null(await testDatabase.EtlRunRepository.GetByIdAsync(runId, CancellationToken.None));
        Assert.False(await testDatabase.EtlRunRepository.TryStartAsync(
            runId,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.False(await testDatabase.EtlRunRepository
            .TryFailLegacyRunningRunWithoutExecutionConfigurationAsync(
                runId,
                DateTimeOffset.UtcNow,
                "Missing execution configuration.",
                CancellationToken.None));
        Assert.False(await testDatabase.EtlRunRepository.TryUpdateProgressAsync(
            runId,
            Progress(1, 1, 0, 0, 0),
            CancellationToken.None));
        Assert.False(await testDatabase.EtlRunRepository.TryMarkTerminalAsync(
            runId,
            EtlRunStatus.Failed,
            DateTimeOffset.UtcNow,
            finalProgress: null,
            "Missing run.",
            errorReportPath: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task AddAsync_DuplicateIdThrowsRunSpecificException()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var run = CreateQueuedRun();
        var duplicate = CreateQueuedRun();
        duplicate.Id = run.Id;
        await testDatabase.EtlRunRepository.AddAsync(run, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DuplicateEtlRunException>(
            () => testDatabase.EtlRunRepository.AddAsync(duplicate, CancellationToken.None));

        Assert.Equal(run.Id, exception.RunId);
        Assert.IsType<global::MongoDB.Driver.MongoWriteException>(exception.InnerException);
    }

    private static BatchExecutionProgress Progress(
        long processedRows,
        long validRows,
        long invalidRows,
        long filteredRows,
        long deduplicatedRows,
        long insertedRows = 0,
        long updatedRows = 0) => new(
            processedRows,
            validRows,
            invalidRows,
            filteredRows,
            deduplicatedRows,
            isCompleted: false,
            insertedRows,
            updatedRows);

    private static (long, long, long, long, long, long, long) Counters(EtlRun run) =>
        (run.ProcessedRows, run.ValidRows, run.InvalidRows, run.FilteredRows,
            run.DeduplicatedRows, run.InsertedRows, run.UpdatedRows);

    private static EtlRun CreateRun(EtlRunStatus status)
    {
        var run = CreateRun();
        run.Status = status;
        return run;
    }

    private static EtlRun CreateQueuedRun()
    {
        var run = CreateRun();
        run.Status = EtlRunStatus.Queued;
        run.StartedAt = null;
        run.CompletedAt = null;
        run.ProcessedRows = 0;
        run.ValidRows = 0;
        run.InvalidRows = 0;
        run.FilteredRows = 0;
        run.DeduplicatedRows = 0;
        run.InsertedRows = 0;
        run.UpdatedRows = 0;
        run.SystemError = null;
        return run;
    }

    private static EtlRun CreateRun() => new()
    {
        Id = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        PipelineName = "Customer import",
        Status = EtlRunStatus.PartiallyCompleted,
        OriginalFileName = "customers.csv",
        StoredFilePath = "runs/8c44a7e8/source.csv",
        StartedAt = new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.FromHours(3)),
        CompletedAt = new DateTimeOffset(2026, 8, 25, 9, 5, 0, TimeSpan.FromHours(3)),
        TotalRows = 30,
        ProcessedRows = 19,
        ValidRows = 10,
        InvalidRows = 2,
        FilteredRows = 3,
        DeduplicatedRows = 4,
        InsertedRows = 6,
        UpdatedRows = 4,
        SystemError = "The target became unavailable after the second batch.",
        ErrorReportPath = "reports/8c44a7e8-errors.csv",
        ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(new PipelineDefinition
        {
            SourceType = SourceType.Xlsx,
            SourceOptions = new SourceOptions
            {
                CultureName = "tr-TR",
                DateFormat = "dd.MM.yyyy",
                WorksheetName = "Customers",
                FirstRowIsHeader = true
            },
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "CustomerId", DataType = SourceFieldType.Integer }
            ],
            FieldMappings =
            [
                new FieldMapping { SourceField = "CustomerId", TargetField = "customer_id" }
            ],
            TransformationRules =
            [
                new TransformationRule
                {
                    Id = Guid.NewGuid(),
                    Type = TransformationType.Trim,
                    Order = 1,
                    SourceField = "customer_id",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Mode"] = "Both"
                    }
                }
            ],
            ValidationRules =
            [
                new ValidationRule
                {
                    Id = Guid.NewGuid(),
                    Type = ValidationType.Required,
                    Field = "customer_id",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal),
                    ErrorMessage = "Customer identifier is required."
                }
            ],
            DestinationDatabase = "etl_target",
            DestinationCollection = "customers",
            UpsertKeyField = "customer_id"
        })
    };

    private static void AssertRunEqual(EtlRun expected, EtlRun actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.PipelineId, actual.PipelineId);
        Assert.Equal(expected.PipelineName, actual.PipelineName);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.OriginalFileName, actual.OriginalFileName);
        Assert.Equal(expected.StoredFilePath, actual.StoredFilePath);
        Assert.Equal(expected.StartedAt, actual.StartedAt);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.TotalRows, actual.TotalRows);
        Assert.Equal(expected.ProcessedRows, actual.ProcessedRows);
        Assert.Equal(expected.ValidRows, actual.ValidRows);
        Assert.Equal(expected.InvalidRows, actual.InvalidRows);
        Assert.Equal(expected.FilteredRows, actual.FilteredRows);
        Assert.Equal(expected.DeduplicatedRows, actual.DeduplicatedRows);
        Assert.Equal(expected.InsertedRows, actual.InsertedRows);
        Assert.Equal(expected.UpdatedRows, actual.UpdatedRows);
        Assert.Equal(expected.SystemError, actual.SystemError);
        Assert.Equal(expected.ErrorReportPath, actual.ErrorReportPath);
        AssertExecutionConfigurationEqual(expected.ExecutionConfiguration, actual.ExecutionConfiguration);
    }

    private static void AssertExecutionConfigurationEqual(
        EtlRunExecutionConfiguration? expected,
        EtlRunExecutionConfiguration? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.ToBsonDocument(), actual.ToBsonDocument());
    }
}
