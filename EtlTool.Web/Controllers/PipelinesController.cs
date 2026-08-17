using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

public sealed class PipelinesController : Controller
{
    private readonly IPipelineService _pipelineService;

    public PipelinesController(IPipelineService pipelineService)
    {
        ArgumentNullException.ThrowIfNull(pipelineService);
        _pipelineService = pipelineService;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var pipelines = await _pipelineService.ListAsync(cancellationToken);
        IReadOnlyList<PipelineListItemViewModel> model = pipelines
            .OrderByDescending(pipeline => pipeline.UpdatedAt)
            .Select(pipeline => new PipelineListItemViewModel
            {
                Id = pipeline.Id,
                Name = pipeline.Name,
                SourceType = pipeline.SourceType,
                DestinationDatabase = pipeline.DestinationDatabase,
                DestinationCollection = pipeline.DestinationCollection,
                UpdatedAt = pipeline.UpdatedAt
            })
            .ToList();

        return View(model);
    }

    public IActionResult Create()
    {
        return View(new PipelineFormViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        PipelineFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var pipeline = new PipelineDefinition
        {
            Name = model.Name,
            Description = model.Description
        };

        try
        {
            await _pipelineService.CreateAsync(pipeline, cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "pipeline")
        {
            ModelState.AddModelError(nameof(PipelineFormViewModel.Name), exception.Message);
            return View(model);
        }
        catch (DuplicatePipelineDefinitionException)
        {
            ModelState.AddModelError(
                string.Empty,
                "The pipeline could not be created because of an identity conflict. Please try again.");
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        ViewData["PipelineId"] = id;
        return View(new PipelineFormViewModel
        {
            Name = pipeline.Name,
            Description = pipeline.Description
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        [FromRoute] Guid id,
        PipelineFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        ViewData["PipelineId"] = id;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        pipeline.Name = model.Name;
        pipeline.Description = model.Description;

        try
        {
            if (!await _pipelineService.UpdateAsync(id, pipeline, cancellationToken))
            {
                return NotFound();
            }
        }
        catch (ArgumentException exception) when (exception.ParamName == "pipeline")
        {
            ModelState.AddModelError(nameof(PipelineFormViewModel.Name), exception.Message);
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        return View(new PipelineDeleteViewModel
        {
            Id = id,
            Name = pipeline.Name
        });
    }

    [HttpPost]
    [ActionName(nameof(Delete))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        if (!await _pipelineService.DeleteAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return RedirectToAction(nameof(Index));
    }
}
