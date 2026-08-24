using EtlTool.Application.Pipelines;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

[Route("Pipelines/{pipelineId:guid}/Validations")]
public sealed class ValidationRulesController : Controller
{
    private readonly IPipelineService _pipelineService;
    private readonly IValidationRuleService _ruleService;

    public ValidationRulesController(IPipelineService pipelineService, IValidationRuleService ruleService)
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
        return View(new ValidationRulesViewModel { PipelineId = pipelineId, Rules = pipeline.ValidationRules });
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create(Guid pipelineId, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return NotFound();
        return View(CreateFormModel(pipeline));
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid pipelineId, ValidationRuleFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return await RedisplayFormAsync(pipelineId, model, cancellationToken);

        try
        {
            if (await _ruleService.CreateAsync(pipelineId, ToInput(model), cancellationToken) is null) return NotFound();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await RedisplayFormAsync(pipelineId, model, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await RedisplayFormAsync(pipelineId, model, cancellationToken);
        }

        return RedirectToAction(nameof(Index), new { pipelineId });
    }

    private async Task<IActionResult> RedisplayFormAsync(Guid pipelineId, ValidationRuleFormViewModel model, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return NotFound();
        PopulateFields(model, pipeline);
        return View(model);
    }

    private static ValidationRuleFormViewModel CreateFormModel(PipelineDefinition pipeline)
    {
        var model = new ValidationRuleFormViewModel();
        PopulateFields(model, pipeline);
        return model;
    }

    private static void PopulateFields(ValidationRuleFormViewModel model, PipelineDefinition pipeline)
    {
        model.AvailableMappedFields = pipeline.FieldMappings
            .Where(mapping => mapping is not null && mapping.IsIncluded && !string.IsNullOrWhiteSpace(mapping.TargetField))
            .Select(mapping => mapping.TargetField)
            .ToList();
        model.UpsertKeyField = pipeline.UpsertKeyField;
    }

    private static ValidationRuleInput ToInput(ValidationRuleFormViewModel model) =>
        new(model.Type, model.Field, model.Minimum, model.Maximum, model.ErrorMessage);
}
