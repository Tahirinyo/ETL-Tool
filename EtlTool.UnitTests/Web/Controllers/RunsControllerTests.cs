using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
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

    private sealed class RecordingRunRepository(EtlRun? run = null) : IEtlRunRepository
    {
        public int GetByIdCallCount { get; private set; }
        public Guid? RequestedId { get; private set; }
        public CancellationToken RequestedCancellationToken { get; private set; }
        public bool ThrowWhenCancelled { get; init; }

        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken)
        {
            GetByIdCallCount++;
            RequestedId = runId;
            RequestedCancellationToken = cancellationToken;
            if (ThrowWhenCancelled) cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(runId == run?.Id ? run : null);
        }

        public Task AddAsync(EtlRun value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryStartAsync(Guid runId, DateTimeOffset startedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryUpdateProgressAsync(Guid runId, BatchExecutionProgress progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryMarkTerminalAsync(Guid runId, EtlRunStatus status, DateTimeOffset completedAt, BatchExecutionProgress? finalProgress, string? systemError, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
