using EtlTool.Application.Pipelines;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
        var pipeline = await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return NotFound();

        return View(CreateFormModel(pipeline));
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid pipelineId,
        TransformationRuleFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return await RedisplayFormAsync(pipelineId, model, cancellationToken);

        try
        {
            var rule = await _ruleService.CreateAsync(pipelineId, ToInput(model), cancellationToken);
            if (rule is null) return NotFound();
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

    [HttpGet("{ruleId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid pipelineId, Guid ruleId, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
        var rule = pipeline?.TransformationRules.SingleOrDefault(candidate => candidate.Id == ruleId);
        if (rule is null) return NotFound();
        var configuration = rule.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var model = CreateFormModel(pipeline!);
        model.Type = rule.Type;
        model.SourceField = rule.SourceField ?? string.Empty;
        model.DefaultValue = rule.Type == TransformationType.SetDefaultValue
            ? configuration.GetValueOrDefault("Value")
            : null;
        model.Find = configuration.GetValueOrDefault("Find");
        model.Replace = configuration.GetValueOrDefault("Replace");
        model.FilterValue = rule.Type == TransformationType.FilterRow
            ? configuration.GetValueOrDefault("Value")
            : null;
        model.FilterOperator = ReadFilterOperator(configuration);
        model.SelectedFields = ReadSelectedFields(configuration);

        return View(model);
    }

    [HttpPost("{ruleId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid pipelineId,
        Guid ruleId,
        TransformationRuleFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return await RedisplayFormAsync(pipelineId, model, cancellationToken);

        try
        {
            if (!await _ruleService.UpdateAsync(pipelineId, ruleId, ToInput(model), cancellationToken)) return NotFound();
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

    private async Task<IActionResult> RedisplayFormAsync(
        Guid pipelineId,
        TransformationRuleFormViewModel model,
        CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return NotFound();

        PopulateAvailableMappedFields(model, pipeline);
        return View(model);
    }

    private static TransformationRuleFormViewModel CreateFormModel(PipelineDefinition pipeline)
    {
        var model = new TransformationRuleFormViewModel();
        PopulateAvailableMappedFields(model, pipeline);
        return model;
    }

    private static void PopulateAvailableMappedFields(
        TransformationRuleFormViewModel model,
        PipelineDefinition pipeline) =>
        model.AvailableMappedFields = pipeline.FieldMappings
            .Where(mapping => mapping is not null
                && mapping.IsIncluded
                && !string.IsNullOrWhiteSpace(mapping.TargetField))
            .Select(mapping => mapping.TargetField)
            .ToList();

    private static FilterOperator ReadFilterOperator(Dictionary<string, string> configuration) =>
        configuration.TryGetValue("Operator", out var value)
        && Enum.TryParse<FilterOperator>(value, ignoreCase: false, out var filterOperator)
        && Enum.IsDefined(filterOperator)
        && filterOperator != FilterOperator.Unspecified
            ? filterOperator
            : FilterOperator.Unspecified;

    private static List<string> ReadSelectedFields(Dictionary<string, string> configuration)
    {
        if (!configuration.TryGetValue("Fields", out var value) || value is null) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static TransformationRuleInput ToInput(TransformationRuleFormViewModel model) => model.Type switch
    {
        TransformationType.SetDefaultValue => new(
            model.Type, model.SourceField, model.DefaultValue, null, null),
        TransformationType.FindAndReplace => new(
            model.Type, model.SourceField, null, model.Find, model.Replace),
        TransformationType.FilterRow => new(
            model.Type, model.SourceField, null, null, null, model.FilterOperator, model.FilterValue),
        TransformationType.Deduplicate => new(
            model.Type, null, null, null, null, SelectedFields: model.SelectedFields),
        _ => new(model.Type, model.SourceField, null, null, null)
    };
}
