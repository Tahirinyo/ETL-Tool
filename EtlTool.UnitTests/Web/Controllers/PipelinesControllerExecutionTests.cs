using System.Reflection;
using EtlTool.Application.Execution;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerExecutionTests
{
    [Fact]
    public void Execute_IsAntiforgeryProtectedPostAtPipelineExecutionRoute()
    {
        MethodInfo action = typeof(PipelinesController)
            .GetMethod(nameof(PipelinesController.Execute))
            ?? throw new InvalidOperationException("The Execute action was not found.");

        var post = Assert.IsType<HttpPostAttribute>(action.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal("/Pipelines/{id:guid}/Execute", post.Template);
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task Execute_AdmittedRunRedirectsImmediatelyToExistingProgressAction()
    {
        var pipelineId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var admission = new StubAdmissionService(
            RunAdmissionResult.Admitted(pipelineId, "Customers", runId));
        var controller = Controller(admission);

        var result = await controller.Execute(pipelineId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(RunsController.Progress), redirect.ActionName);
        Assert.Equal("Runs", redirect.ControllerName);
        Assert.Equal(runId, redirect.RouteValues!["runId"]);
        Assert.Equal(1, admission.CallCount);
        Assert.DoesNotContain(
            typeof(PipelinesController).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IBackgroundJobExecutor)
                || parameter.ParameterType == typeof(IBatchOrchestrator));
    }

    [Fact]
    public async Task Execute_NotFoundReturnsNotFound()
    {
        var controller = Controller(new StubAdmissionService(
            RunAdmissionResult.PipelineNotFound()));

        Assert.IsType<NotFoundResult>(
            await controller.Execute(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Execute_NotReadyReusesPreviewReadinessPresentation()
    {
        var pipelineId = Guid.NewGuid();
        PipelineReadinessProblem[] problems = [new("Mapping", "Remap the source.")];
        var controller = Controller(new StubAdmissionService(
            RunAdmissionResult.PipelineNotReady(pipelineId, "Customers", problems)));

        var result = Assert.IsType<ViewResult>(
            await controller.Execute(pipelineId, CancellationToken.None));
        var model = Assert.IsType<PipelinePreviewViewModel>(result.Model);

        Assert.Equal("Preview", result.ViewName);
        Assert.Equal(problems, model.ReadinessProblems);
        Assert.Null(model.FailureMessage);
    }

    [Fact]
    public async Task Execute_SourceUnavailableReturnsSafeGonePreview()
    {
        var pipelineId = Guid.NewGuid();
        var controller = Controller(new StubAdmissionService(
            RunAdmissionResult.SourceUnavailable(pipelineId, "Customers")));

        var result = Assert.IsType<ViewResult>(
            await controller.Execute(pipelineId, CancellationToken.None));
        var model = Assert.IsType<PipelinePreviewViewModel>(result.Model);

        Assert.Equal(StatusCodes.Status410Gone, controller.Response.StatusCode);
        Assert.True(model.RequiresSourceUpload);
        Assert.DoesNotContain("path", model.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ActiveRunReturnsSafeConflictPreview()
    {
        var pipelineId = Guid.NewGuid();
        var controller = Controller(new StubAdmissionService(
            RunAdmissionResult.RunAlreadyActive(pipelineId, "Customers")));

        var result = Assert.IsType<ViewResult>(
            await controller.Execute(pipelineId, CancellationToken.None));
        var model = Assert.IsType<PipelinePreviewViewModel>(result.Model);

        Assert.Equal(StatusCodes.Status409Conflict, controller.Response.StatusCode);
        Assert.Contains("already has a queued or running execution", model.FailureMessage, StringComparison.Ordinal);
        Assert.False(model.RequiresSourceUpload);
    }

    [Fact]
    public async Task Execute_AdmissionFailureReturnsSafeServiceUnavailablePreview()
    {
        var pipelineId = Guid.NewGuid();
        var controller = Controller(new StubAdmissionService(
            RunAdmissionResult.Failed(
                pipelineId,
                "Customers",
                new IOException("C:\\private\\source.upload failed."))));

        var result = Assert.IsType<ViewResult>(
            await controller.Execute(pipelineId, CancellationToken.None));
        var model = Assert.IsType<PipelinePreviewViewModel>(result.Model);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, controller.Response.StatusCode);
        Assert.Equal("The run could not be admitted to background execution. Try again.", model.FailureMessage);
        Assert.DoesNotContain("private", model.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static PipelinesController Controller(IRunAdmissionService admissionService) =>
        new(new StubPipelineService(), runAdmissionService: admissionService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    private sealed class StubAdmissionService(RunAdmissionResult result) : IRunAdmissionService
    {
        public int CallCount { get; private set; }

        public Task<RunAdmissionResult> AdmitAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubPipelineService : IPipelineService
    {
        public Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
