using EtlTool.Application.Execution;
using EtlTool.Infrastructure.Reporting;
using EtlTool.Web.Models.Runs;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

[Route("Runs")]
public sealed class RunsController : Controller
{
    private readonly IEtlRunRepository _runRepository;
    private readonly IErrorReportStore? _errorReportStore;

    public RunsController(
        IEtlRunRepository runRepository,
        IErrorReportStore? errorReportStore = null)
    {
        ArgumentNullException.ThrowIfNull(runRepository);
        _runRepository = runRepository;
        _errorReportStore = errorReportStore;
    }

    [HttpGet("{runId:guid}")]
    public IActionResult Progress(Guid runId)
    {
        if (runId == Guid.Empty) return NotFound();

        return View(new RunProgressViewModel
        {
            RunId = runId
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
}
