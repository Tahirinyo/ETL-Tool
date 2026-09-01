using EtlTool.Application.Execution;
using EtlTool.Application.Pipelines;
using EtlTool.Infrastructure.Reporting;
using EtlTool.Web.Models.Runs;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

[Route("Runs")]
public sealed class RunsController : Controller
{
    private readonly IEtlRunRepository _runRepository;
    private readonly IErrorReportStore? _errorReportStore;
    private readonly IPipelineService? _pipelineService;

    public RunsController(
        IEtlRunRepository runRepository,
        IErrorReportStore? errorReportStore = null,
        IPipelineService? pipelineService = null)
    {
        ArgumentNullException.ThrowIfNull(runRepository);
        _runRepository = runRepository;
        _errorReportStore = errorReportStore;
        _pipelineService = pipelineService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (_pipelineService is null)
        {
            throw new InvalidOperationException("Run history is not configured.");
        }

        var pipelines = await _pipelineService.ListAsync(cancellationToken);
        var items = new List<GlobalRunHistoryItemViewModel>();
        foreach (var pipeline in pipelines)
        {
            var runs = await _runRepository.ListByPipelineIdAsync(pipeline.Id, cancellationToken);
            items.AddRange(runs.Select(run => new GlobalRunHistoryItemViewModel
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                StartedAt = run.StartedAt,
                Run = RunHistoryItemViewModel.From(run)
            }));
        }

        return View(new GlobalRunHistoryViewModel
        {
            Runs = items
                .OrderByDescending(item => item.StartedAt)
                .ToList()
        });
    }

    [HttpGet("/Pipelines/{pipelineId:guid}/Runs")]
    public async Task<IActionResult> History(
        Guid pipelineId,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty) return NotFound();

        var pipeline = await GetPipelineAsync(pipelineId, cancellationToken);
        if (pipeline is null) return NotFound();

        var runs = await _runRepository.ListByPipelineIdAsync(pipelineId, cancellationToken);
        return View(new RunHistoryViewModel
        {
            PipelineId = pipelineId,
            PipelineName = pipeline.Name,
            Runs = runs.Select(RunHistoryItemViewModel.From).ToList()
        });
    }

    [HttpGet("/Pipelines/{pipelineId:guid}/Runs/{runId:guid}")]
    public async Task<IActionResult> Details(
        Guid pipelineId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty || runId == Guid.Empty) return NotFound();

        var run = await _runRepository.GetByIdAsync(runId, cancellationToken);
        if (run is null || run.PipelineId != pipelineId) return NotFound();

        return View(RunDetailsViewModel.From(run));
    }

    [HttpGet("{runId:guid}")]
    public IActionResult Progress(Guid runId, Guid? pipelineId = null)
    {
        if (runId == Guid.Empty) return NotFound();

        return View(new RunProgressViewModel
        {
            RunId = runId,
            PipelineId = pipelineId
        });
    }

    [HttpGet("{runId:guid}/Status")]
    public async Task<IActionResult> Status(Guid runId, CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty) return NotFound();

        var run = await _runRepository.GetByIdAsync(runId, cancellationToken);
        return run is null ? NotFound() : Json(RunStatusResponse.From(run));
    }

    [HttpGet("{runId:guid}/ErrorReport")]
    public async Task<IActionResult> DownloadErrorReport(
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty) return NotFound();

        var run = await _runRepository.GetByIdAsync(runId, cancellationToken);
        if (run is null || string.IsNullOrWhiteSpace(run.ErrorReportPath)) return NotFound();

        var report = _errorReportStore?.OpenRead(run);
        if (report is null) return NotFound();

        return File(
            report,
            "text/csv; charset=utf-8",
            $"error-report-{runId:N}.csv");
    }

    private async Task<EtlTool.Domain.Entities.PipelineDefinition?> GetPipelineAsync(
        Guid pipelineId,
        CancellationToken cancellationToken)
    {
        if (_pipelineService is null)
        {
            throw new InvalidOperationException("Run history is not configured.");
        }

        return await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
    }
}
