using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

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
        Assert.Equal(run.TotalRows, persisted.TotalRows);
        Assert.Equal(progress.InsertedRows, persisted.InsertedRows);
        Assert.Equal(progress.UpdatedRows, persisted.UpdatedRows);
        Assert.Equal(run.ErrorReportPath, persisted.ErrorReportPath);
        Assert.Equal(run.SystemError, persisted.SystemError);
        Assert.Equal(run.OriginalFileName, persisted.OriginalFileName);
        Assert.Equal(run.StoredFilePath, persisted.StoredFilePath);
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
        Assert.Equal(2, persisted.InsertedRows);
        Assert.Equal(1, persisted.UpdatedRows);
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
    public async Task MissingRuns_ReturnNullForReadAndFalseForGuardedUpdates()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var runId = Guid.NewGuid();

        Assert.Null(await testDatabase.EtlRunRepository.GetByIdAsync(runId, CancellationToken.None));
        Assert.False(await testDatabase.EtlRunRepository.TryStartAsync(
            runId,
            DateTimeOffset.UtcNow,
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
        ErrorReportPath = "reports/8c44a7e8-errors.csv"
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
    }
}
