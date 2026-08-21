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
    public async Task Create_InvalidModelDoesNotCallService()
    {
        var ruleService = new RecordingRuleService();
        var controller = new TransformationRulesController(new RecordingPipelineService(), ruleService);
        var model = new TransformationRuleFormViewModel();
        controller.ModelState.AddModelError(nameof(model.Type), "Invalid type.");

        var result = await controller.Create(Guid.NewGuid(), model, CancellationToken.None);

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
    public void PostActionsRequireAntiForgeryProtection()
    {
        var postActions = typeof(TransformationRulesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null)
            .ToList();

        Assert.Equal(3, postActions.Count);
        Assert.All(postActions, action => Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }

    private sealed class RecordingRuleService : ITransformationRuleService
    {
        public TransformationRuleInput? CreateInput { get; private set; }
        public bool UpdateResult { get; init; } = true;
        public bool DeleteResult { get; init; } = true;

        public Task<TransformationRule?> CreateAsync(Guid pipelineId, TransformationRuleInput input, CancellationToken cancellationToken)
        {
            CreateInput = input;
            return Task.FromResult<TransformationRule?>(new TransformationRule { Id = Guid.NewGuid() });
        }

        public Task<bool> UpdateAsync(Guid pipelineId, Guid ruleId, TransformationRuleInput input, CancellationToken cancellationToken) =>
            Task.FromResult(UpdateResult);

        public Task<bool> DeleteAsync(Guid pipelineId, Guid ruleId, CancellationToken cancellationToken) =>
            Task.FromResult(DeleteResult);
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
