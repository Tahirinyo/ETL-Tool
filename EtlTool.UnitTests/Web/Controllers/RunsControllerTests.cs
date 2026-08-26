using EtlTool.Application.Execution;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.Reporting;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Runs;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class RunsControllerTests
{
    [Fact]
    public void Progress_ValidRunIdReturnsViewWithRequestedRunIdWithoutRepositoryAccess()
    {
        var runId = Guid.NewGuid();
        var repository = new RecordingRunRepository();

        var result = new RunsController(repository).Progress(runId);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RunProgressViewModel>(view.Model);
        Assert.Equal(runId, model.RunId);
        Assert.Equal(0, repository.GetByIdCallCount);
    }

    [Fact]
    public void Progress_EmptyRunIdReturnsNotFoundWithoutRepositoryAccess()
    {
        var repository = new RecordingRunRepository();

        var result = new RunsController(repository).Progress(Guid.Empty);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, repository.GetByIdCallCount);
    }

    [Fact]
    public async Task Status_ExistingRunReturnsOnlyPollingStatusFields()
    {
        var run = Run();
        var repository = new RecordingRunRepository(run);
        var result = await new RunsController(repository).Status(run.Id, CancellationToken.None);

        var response = Assert.IsType<RunStatusResponse>(Assert.IsType<JsonResult>(result).Value);
        Assert.Equal(run.Id, response.RunId);
        Assert.Equal("PartiallyCompleted", response.Status);
        Assert.Equal(run.StartedAt, response.StartedAt);
        Assert.Equal(run.CompletedAt, response.CompletedAt);
        Assert.Equal(run.TotalRows, response.TotalRows);
        Assert.Equal(run.ProcessedRows, response.ProcessedRows);
        Assert.Equal(run.ValidRows, response.ValidRows);
        Assert.Equal(run.InvalidRows, response.InvalidRows);
        Assert.Equal(run.FilteredRows, response.FilteredRows);
        Assert.Equal(run.DeduplicatedRows, response.DeduplicatedRows);
        Assert.Equal(run.InsertedRows, response.InsertedRows);
        Assert.Equal(run.UpdatedRows, response.UpdatedRows);
        Assert.Equal(run.Id, repository.RequestedId);

        Assert.DoesNotContain(typeof(RunStatusResponse).GetProperties(), property =>
            property.Name is "PipelineId" or "PipelineName" or "OriginalFileName" or "StoredFilePath" or "ErrorReportPath" or "SystemError");
    }

    [Fact]
    public async Task Status_MissingRunReturnsNotFound()
    {
        var runId = Guid.NewGuid();
        var repository = new RecordingRunRepository();

        var result = await new RunsController(repository).Status(runId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(runId, repository.RequestedId);
    }

    [Fact]
    public async Task Status_EmptyRunIdReturnsNotFoundWithoutRepositoryAccess()
    {
        var repository = new RecordingRunRepository();

        var result = await new RunsController(repository).Status(Guid.Empty, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, repository.GetByIdCallCount);
    }

    [Fact]
    public async Task Status_PropagatesCancellationToRepository()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var repository = new RecordingRunRepository { ThrowWhenCancelled = true };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new RunsController(repository).Status(Guid.NewGuid(), cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, repository.RequestedCancellationToken);
    }

    [Fact]
    public async Task DownloadErrorReport_OwnerGetsFrameworkStreamedCsvWithSafeFilename()
    {
        var run = Run();
        run.ErrorReportPath = $"error-report-{run.Id:N}.csv";
        var result = await new RunsController(
            new RecordingRunRepository(run),
            new RecordingReportStore(run.Id)).DownloadErrorReport(run.Id, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal($"error-report-{run.Id:N}.csv", file.FileDownloadName);
        Assert.IsType<MemoryStream>(file.FileStream);
    }

    [Fact]
    public async Task DownloadErrorReport_MissingOrWrongRunReportReturnsNotFound()
    {
        var owner = Run();
        owner.ErrorReportPath = $"error-report-{owner.Id:N}.csv";
        var other = Run();
        other.ErrorReportPath = owner.ErrorReportPath;
        var store = new RecordingReportStore(owner.Id);

        var missing = await new RunsController(new RecordingRunRepository())
            .DownloadErrorReport(Guid.NewGuid(), CancellationToken.None);
        var wrong = await new RunsController(new RecordingRunRepository(other), store)
            .DownloadErrorReport(other.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(missing);
        Assert.IsType<NotFoundResult>(wrong);
    }

    [Fact]
    public async Task History_ReturnsOnlyRequestedPipelineRunsNewestFirst()
    {
        var pipeline = new PipelineDefinition { Id = Guid.NewGuid(), Name = "Customers" };
        var older = Run();
        older.PipelineId = pipeline.Id;
        older.StartedAt = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var newer = Run();
        newer.PipelineId = pipeline.Id;
        newer.StartedAt = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        var repository = new RecordingRunRepository(runs: [newer, older]);

        var result = await new RunsController(
            repository,
            pipelineService: new RecordingPipelineService(pipeline)).History(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<RunHistoryViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(pipeline.Name, model.PipelineName);
        Assert.Equal([newer.Id, older.Id], model.Runs.Select(run => run.Id));
        Assert.Equal("PartiallyCompleted", model.Runs[0].Status);
        Assert.Equal(20, model.Runs[0].TotalRows);
        Assert.Equal(15, model.Runs[0].ProcessedRows);
        Assert.Equal(2, model.Runs[0].InvalidRows);
        Assert.Equal("00:02:00", model.Runs[0].DurationDisplay);
        Assert.Equal(pipeline.Id, repository.RequestedPipelineId);
    }

    [Fact]
    public async Task History_ExistingPipelineWithNoRunsReturnsEmptyModel()
    {
        var pipeline = new PipelineDefinition { Id = Guid.NewGuid(), Name = "Customers" };

        var result = await new RunsController(
            new RecordingRunRepository(),
            pipelineService: new RecordingPipelineService(pipeline)).History(pipeline.Id, CancellationToken.None);

        Assert.Empty(Assert.IsType<RunHistoryViewModel>(Assert.IsType<ViewResult>(result).Model).Runs);
    }

    [Fact]
    public async Task History_MissingPipelineReturnsNotFoundWithoutQueryingRuns()
    {
        var repository = new RecordingRunRepository();
        var result = await new RunsController(
            repository,
            pipelineService: new RecordingPipelineService(new PipelineDefinition { Id = Guid.NewGuid() }))
            .History(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Null(repository.RequestedPipelineId);
    }

    [Fact]
    public async Task Details_MapsTerminalRunSafeErrorAndSecureReportFlag()
    {
        var run = Run();
        var result = await new RunsController(new RecordingRunRepository(run))
            .Details(run.PipelineId, run.Id, CancellationToken.None);

        var model = Assert.IsType<RunDetailsViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(run.Id, model.Id);
        Assert.Equal(run.PipelineName, model.PipelineName);
        Assert.Equal(run.SystemError, model.SystemError);
        Assert.True(model.HasErrorReport);
        Assert.Equal("00:02:00", model.DurationDisplay);
        Assert.Equal(run.UpdatedRows, model.UpdatedRows);
    }

    [Fact]
    public async Task Details_MissingOrOtherPipelineRunReturnsNotFound()
    {
        var run = Run();
        var controller = new RunsController(new RecordingRunRepository(run));

        var missing = await controller.Details(run.PipelineId, Guid.NewGuid(), CancellationToken.None);
        var otherPipeline = await controller.Details(Guid.NewGuid(), run.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(missing);
        Assert.IsType<NotFoundResult>(otherPipeline);
    }

    [Fact]
    public async Task Details_ActiveRunWithoutCompletionRendersInProgressDuration()
    {
        var run = Run();
        run.Status = EtlRunStatus.Running;
        run.CompletedAt = null;
        run.ErrorReportPath = null;

        var result = await new RunsController(new RecordingRunRepository(run))
            .Details(run.PipelineId, run.Id, CancellationToken.None);

        var model = Assert.IsType<RunDetailsViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("In progress", model.CompletedAtDisplay);
        Assert.Equal("In progress", model.DurationDisplay);
        Assert.False(model.HasErrorReport);
    }

    private static EtlRun Run() => new()
    {
        Id = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        PipelineName = "Customers",
        OriginalFileName = "customers.csv",
        StoredFilePath = "runs/source.csv",
        Status = EtlRunStatus.PartiallyCompleted,
        StartedAt = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 8, 25, 10, 2, 0, TimeSpan.Zero),
        TotalRows = 20,
        ProcessedRows = 15,
        ValidRows = 8,
        InvalidRows = 2,
        FilteredRows = 3,
        DeduplicatedRows = 2,
        InsertedRows = 5,
        UpdatedRows = 3,
        SystemError = "Internal database diagnostic.",
        ErrorReportPath = "reports/errors.csv"
    };

    private sealed class RecordingRunRepository(EtlRun? run = null, IReadOnlyList<EtlRun>? runs = null) : IEtlRunRepository
    {
        public int GetByIdCallCount { get; private set; }
        public Guid? RequestedId { get; private set; }
        public CancellationToken RequestedCancellationToken { get; private set; }
        public Guid? RequestedPipelineId { get; private set; }
        public bool ThrowWhenCancelled { get; init; }

        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            RequestedId = runId;
            RequestedCancellationToken = cancellationToken;
            if (ThrowWhenCancelled) cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(runId == run?.Id ? run : null);
        }

        public Task<IReadOnlyList<EtlRun>> ListByPipelineIdAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            RequestedPipelineId = pipelineId;
            return Task.FromResult(runs ?? (IReadOnlyList<EtlRun>)[]);
        }

        public Task AddAsync(EtlRun value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryStartAsync(Guid runId, DateTimeOffset startedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryUpdateProgressAsync(Guid runId, BatchExecutionProgress progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryMarkTerminalAsync(Guid runId, EtlRunStatus status, DateTimeOffset completedAt, BatchExecutionProgress? finalProgress, string? systemError, string? errorReportPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingPipelineService(PipelineDefinition pipeline) : IPipelineService
    {
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingReportStore(Guid ownerRunId) : IErrorReportStore
    {
        public IErrorReportOutput CreateOutput(EtlRun run) => throw new NotSupportedException();

        public Stream? OpenRead(EtlRun run) => run.Id == ownerRunId
            && run.ErrorReportPath == $"error-report-{run.Id:N}.csv"
                ? new MemoryStream("csv"u8.ToArray())
                : null;

        public Task DeletePublishedAsync(EtlRun run, string reportReference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
