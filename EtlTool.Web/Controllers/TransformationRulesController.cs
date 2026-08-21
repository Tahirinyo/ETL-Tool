using EtlTool.Application.Pipelines;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

[Route("Pipelines/{pipelineId:guid}/Transformations")]
public sealed class TransformationRulesController : Controller
{
    private readonly IPipelineService _pipelineService;
    private readonly ITransformationRuleService _ruleService;

    public TransformationRulesController(
        IPipelineService pipelineService,
        ITransformationRuleService ruleService)
    {
        ArgumentNullException.ThrowIfNull(pipelineService);
        ArgumentNullException.ThrowIfNull(ruleService);
        _pipelineService = pipelineService;
        _ruleService = ruleService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid pipelineId, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return NotFound();

        return View(new TransformationRulesViewModel
        {
            PipelineId = pipelineId,
            Rules = pipeline.TransformationRules
                .OrderBy(rule => rule.Order)
                .Select(TransformationRuleCardViewModel.FromRule)
                .ToList()
        });
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create(Guid pipelineId, CancellationToken cancellationToken)
    {
        if (!await PipelineExistsAsync(pipelineId, cancellationToken)) return NotFound();
        return View(new TransformationRuleFormViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid pipelineId,
        TransformationRuleFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(model);

        try
        {
            var rule = await _ruleService.CreateAsync(pipelineId, ToInput(model), cancellationToken);
            if (rule is null) return NotFound();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return View(model);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return View(model);
        }

        return RedirectToAction(nameof(Index), new { pipelineId });
    }

    [HttpGet("{ruleId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid pipelineId, Guid ruleId, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
        var rule = pipeline?.TransformationRules.SingleOrDefault(candidate => candidate.Id == ruleId);
        if (rule is null) return NotFound();
        var configuration = rule.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return View(new TransformationRuleFormViewModel
        {
            Type = rule.Type,
            SourceField = rule.SourceField ?? string.Empty,
            DefaultValue = configuration.GetValueOrDefault("Value"),
            Find = configuration.GetValueOrDefault("Find"),
            Replace = configuration.GetValueOrDefault("Replace")
        });
    }

    [HttpPost("{ruleId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid pipelineId,
        Guid ruleId,
        TransformationRuleFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(model);

        try
        {
            if (!await _ruleService.UpdateAsync(pipelineId, ruleId, ToInput(model), cancellationToken)) return NotFound();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return View(model);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return View(model);
        }

        return RedirectToAction(nameof(Index), new { pipelineId });
    }

    [HttpPost("{ruleId:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid pipelineId, Guid ruleId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _ruleService.DeleteAsync(pipelineId, ruleId, cancellationToken)) return NotFound();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        return RedirectToAction(nameof(Index), new { pipelineId });
    }

    [HttpPost("Reorder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder(
        Guid pipelineId,
        TransformationRuleReorderViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            if (!await _ruleService.ReorderAsync(pipelineId, model.OrderedRuleIds, cancellationToken))
            {
                return NotFound();
            }
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
        catch (InvalidOperationException)
        {
            return BadRequest();
        }

        return RedirectToAction(nameof(Index), new { pipelineId });
    }

    private async Task<bool> PipelineExistsAsync(Guid pipelineId, CancellationToken cancellationToken) =>
        await _pipelineService.GetByIdAsync(pipelineId, cancellationToken) is not null;

    private static TransformationRuleInput ToInput(TransformationRuleFormViewModel model) => new(
        model.Type,
        model.SourceField,
        model.DefaultValue,
        model.Find,
        model.Replace);
}
