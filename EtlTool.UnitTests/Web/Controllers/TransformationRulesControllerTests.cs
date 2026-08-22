using System.Reflection;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class TransformationRulesControllerTests
{
    [Fact]
    public async Task Index_MapsRulesInPersistedExecutionOrder()
    {
        var pipelineId = Guid.NewGuid();
        var laterRule = new TransformationRule
        {
            Id = Guid.NewGuid(),
            Type = TransformationType.ToLower,
            Order = 20,
            SourceField = "name"
        };
        var earlierRule = new TransformationRule
        {
            Id = Guid.NewGuid(),
            Type = TransformationType.Trim,
            Order = 2,
            SourceField = "name"
        };
        var pipelineService = new RecordingPipelineService
        {
            Pipeline = new PipelineDefinition
            {
                Id = pipelineId,
                TransformationRules = [laterRule, earlierRule]
            }
        };
        var controller = new TransformationRulesController(pipelineService, new RecordingRuleService());

        var result = await controller.Index(pipelineId, CancellationToken.None);

        var model = Assert.IsType<TransformationRulesViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal([earlierRule.Id, laterRule.Id], model.Rules.Select(rule => rule.Id));
        Assert.Equal([2, 20], model.Rules.Select(rule => rule.Order));
    }

    [Fact]
    public async Task Create_ValidPostMapsOnlyAllowedFieldsAndRedirects()
    {
        var pipelineId = Guid.NewGuid();
        var ruleService = new RecordingRuleService();
        var controller = new TransformationRulesController(new RecordingPipelineService(), ruleService);
        var model = new TransformationRuleFormViewModel
        {
            Type = TransformationType.SetDefaultValue,
            SourceField = "name",
            DefaultValue = ""
        };

        var result = await controller.Create(pipelineId, model, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TransformationRulesController.Index), redirect.ActionName);
        var input = Assert.IsType<TransformationRuleInput>(ruleService.CreateInput);
        Assert.Equal(TransformationType.SetDefaultValue, input.Type);
        Assert.Equal("name", input.SourceField);
        Assert.Equal("", input.DefaultValue);
        Assert.Null(input.Find);
        Assert.Null(input.Replace);
    }

    [Fact]
    public async Task Create_FilterPostMapsOnlyFilterConfiguration()
    {
        var ruleService = new RecordingRuleService();
        var controller = new TransformationRulesController(new RecordingPipelineService(), ruleService);
        var model = new TransformationRuleFormViewModel
        {
            Type = TransformationType.FilterRow,
            SourceField = "amount",
            FilterOperator = FilterOperator.GreaterThanOrEqual,
            FilterValue = "12,5",
            DefaultValue = "ignored",
            Find = "ignored",
            Replace = "ignored"
        };

        await controller.Create(Guid.NewGuid(), model, CancellationToken.None);

        var input = Assert.IsType<TransformationRuleInput>(ruleService.CreateInput);
        Assert.Equal(FilterOperator.GreaterThanOrEqual, input.FilterOperator);
        Assert.Equal("12,5", input.FilterValue);
        Assert.Null(input.DefaultValue);
        Assert.Null(input.Find);
        Assert.Null(input.Replace);
    }

    [Fact]
    public async Task Create_DeduplicationPostMapsSelectedFieldsWithoutSourceField()
    {
        var ruleService = new RecordingRuleService();
        var controller = new TransformationRulesController(new RecordingPipelineService(), ruleService);
        var model = new TransformationRuleFormViewModel
        {
            Type = TransformationType.Deduplicate,
            SourceField = "ignored",
            SelectedFields = ["email", "company"],
            DefaultValue = "ignored"
        };

        await controller.Create(Guid.NewGuid(), model, CancellationToken.None);

        var input = Assert.IsType<TransformationRuleInput>(ruleService.CreateInput);
        Assert.Null(input.SourceField);
        Assert.Equal(["email", "company"], input.SelectedFields);
        Assert.Null(input.DefaultValue);
    }

    [Fact]
    public async Task Create_GetPopulatesOnlyIncludedMappedOutputFields()
    {
        var pipelineId = Guid.NewGuid();
        var controller = new TransformationRulesController(new RecordingPipelineService
        {
            Pipeline = new PipelineDefinition
            {
                Id = pipelineId,
                FieldMappings =
                [
                    new() { SourceField = "Id", TargetField = "customerId", IsIncluded = true },
                    new() { SourceField = "Hidden", TargetField = "hidden", IsIncluded = false },
                    new() { SourceField = "Name", TargetField = "name", IsIncluded = true }
                ]
            }
        }, new RecordingRuleService());

        var result = await controller.Create(pipelineId, CancellationToken.None);

        var model = Assert.IsType<TransformationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(["customerId", "name"], model.AvailableMappedFields);
    }

    [Fact]
    public async Task Create_InvalidPostRepopulatesMappedFields()
    {
        var pipelineId = Guid.NewGuid();
        var controller = new TransformationRulesController(new RecordingPipelineService
        {
            Pipeline = new PipelineDefinition
            {
                Id = pipelineId,
                FieldMappings = [new() { SourceField = "Name", TargetField = "name", IsIncluded = true }]
            }
        }, new RecordingRuleService());
        var posted = new TransformationRuleFormViewModel { Type = TransformationType.FilterRow, SourceField = "name" };
        controller.ModelState.AddModelError(nameof(posted.FilterValue), "Required.");

        var result = await controller.Create(pipelineId, posted, CancellationToken.None);

        var model = Assert.IsType<TransformationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Same(posted, model);
        Assert.Equal(["name"], model.AvailableMappedFields);
    }

    [Fact]
    public async Task Edit_GetReconstructsDeduplicationSelection()
    {
        var pipelineId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var controller = new TransformationRulesController(new RecordingPipelineService
        {
            Pipeline = new PipelineDefinition
            {
                Id = pipelineId,
                FieldMappings = [new() { SourceField = "Email", TargetField = "email", IsIncluded = true }],
                TransformationRules =
                [
                    new()
                    {
                        Id = ruleId,
                        Type = TransformationType.Deduplicate,
                        Configuration = new() { ["Fields"] = "[\"email\"]" }
                    }
                ]
            }
        }, new RecordingRuleService());

        var result = await controller.Edit(pipelineId, ruleId, CancellationToken.None);

        var model = Assert.IsType<TransformationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(["email"], model.SelectedFields);
        Assert.Empty(model.SourceField);
    }

    [Fact]
    public async Task Edit_GetRestoresFilterValueWithoutHydratingDefaultValue()
    {
        var pipelineId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var controller = new TransformationRulesController(new RecordingPipelineService
        {
            Pipeline = new PipelineDefinition
            {
                Id = pipelineId,
                FieldMappings = [new() { SourceField = "Amount", TargetField = "amount", IsIncluded = true }],
                TransformationRules =
                [
                    new()
                    {
                        Id = ruleId,
                        Type = TransformationType.FilterRow,
                        SourceField = "amount",
                        Configuration = new()
                        {
                            ["Operator"] = "GreaterThan",
                            ["Value"] = "10"
                        }
                    }
                ]
            }
        }, new RecordingRuleService());

        var result = await controller.Edit(pipelineId, ruleId, CancellationToken.None);

        var model = Assert.IsType<TransformationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(FilterOperator.GreaterThan, model.FilterOperator);
        Assert.Equal("10", model.FilterValue);
        Assert.Null(model.DefaultValue);
    }

    [Fact]
    public async Task Create_InvalidModelDoesNotCallService()
    {
        var ruleService = new RecordingRuleService();
        var pipelineId = Guid.NewGuid();
        var controller = new TransformationRulesController(new RecordingPipelineService
        {
            Pipeline = new PipelineDefinition { Id = pipelineId }
        }, ruleService);
        var model = new TransformationRuleFormViewModel();
        controller.ModelState.AddModelError(nameof(model.Type), "Invalid type.");

        var result = await controller.Create(pipelineId, model, CancellationToken.None);

        Assert.Same(model, Assert.IsType<ViewResult>(result).Model);
        Assert.Null(ruleService.CreateInput);
    }

    [Fact]
    public async Task EditAndDelete_ReturnNotFoundWhenRuleIsNotOwnedByPipeline()
    {
        var pipelineId = Guid.NewGuid();
        var ruleService = new RecordingRuleService { UpdateResult = false, DeleteResult = false };
        var controller = new TransformationRulesController(new RecordingPipelineService(), ruleService);
        var model = new TransformationRuleFormViewModel { Type = TransformationType.Trim, SourceField = "name" };

        var edit = await controller.Edit(pipelineId, Guid.NewGuid(), model, CancellationToken.None);
        var delete = await controller.Delete(pipelineId, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(edit);
        Assert.IsType<NotFoundResult>(delete);
    }

    [Fact]
    public async Task Reorder_ValidPostPassesOrderedIdsAndRedirects()
    {
        var pipelineId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var ruleService = new RecordingRuleService();
        var controller = new TransformationRulesController(new RecordingPipelineService(), ruleService);

        var result = await controller.Reorder(
            pipelineId,
            new TransformationRuleReorderViewModel { OrderedRuleIds = [second, first] },
            CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TransformationRulesController.Index), redirect.ActionName);
        Assert.Equal([second, first], ruleService.ReorderInput);
    }

    [Fact]
    public async Task Reorder_InvalidRequestAndMissingPipelineReturnBadRequestAndNotFound()
    {
        var invalidController = new TransformationRulesController(new RecordingPipelineService(), new RecordingRuleService());
        invalidController.ModelState.AddModelError("OrderedRuleIds", "Invalid identifiers.");

        var invalid = await invalidController.Reorder(
            Guid.NewGuid(),
            new TransformationRuleReorderViewModel(),
            CancellationToken.None);

        var missing = await new TransformationRulesController(
            new RecordingPipelineService(),
            new RecordingRuleService { ReorderResult = false }).Reorder(
                Guid.NewGuid(),
                new TransformationRuleReorderViewModel(),
                CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(invalid);
        Assert.IsType<NotFoundResult>(missing);
    }

    [Fact]
    public void PostActionsRequireAntiForgeryProtection()
    {
        var postActions = typeof(TransformationRulesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null)
            .ToList();

        Assert.Equal(4, postActions.Count);
        Assert.All(postActions, action => Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }

    private sealed class RecordingRuleService : ITransformationRuleService
    {
        public TransformationRuleInput? CreateInput { get; private set; }
        public IReadOnlyList<Guid>? ReorderInput { get; private set; }
        public bool UpdateResult { get; init; } = true;
        public bool DeleteResult { get; init; } = true;
        public bool ReorderResult { get; init; } = true;

        public Task<TransformationRule?> CreateAsync(Guid pipelineId, TransformationRuleInput input, CancellationToken cancellationToken)
        {
            CreateInput = input;
            return Task.FromResult<TransformationRule?>(new TransformationRule { Id = Guid.NewGuid() });
        }

        public Task<bool> UpdateAsync(Guid pipelineId, Guid ruleId, TransformationRuleInput input, CancellationToken cancellationToken) =>
            Task.FromResult(UpdateResult);

        public Task<bool> DeleteAsync(Guid pipelineId, Guid ruleId, CancellationToken cancellationToken) =>
            Task.FromResult(DeleteResult);

        public Task<bool> ReorderAsync(Guid pipelineId, IReadOnlyList<Guid> orderedRuleIds, CancellationToken cancellationToken)
        {
            ReorderInput = orderedRuleIds;
            return Task.FromResult(ReorderResult);
        }
    }

    private sealed class RecordingPipelineService : IPipelineService
    {
        public PipelineDefinition? Pipeline { get; init; }

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Pipeline?.Id == id ? Pipeline : null);
        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Guid id, PipelineDefinition pipeline, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
