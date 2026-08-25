using EtlTool.Application.Execution;
using EtlTool.Web.Models.Runs;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

[Route("Runs")]
public sealed class RunsController : Controller
{
    private readonly IEtlRunRepository _runRepository;

    public RunsController(IEtlRunRepository runRepository)
    {
        ArgumentNullException.ThrowIfNull(runRepository);
        _runRepository = runRepository;
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
}
