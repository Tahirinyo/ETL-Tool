using EtlTool.Application.Pipelines;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class ValidationRulesControllerTests
{
    [Fact]
    public async Task Create_GetPopulatesIncludedMappedFieldsAndPipelineUpsertKey()
    {
        var pipeline = Pipeline();
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), new RuleServiceStub());

        var result = await controller.Create(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<ValidationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(["email"], model.AvailableMappedFields);
        Assert.Equal("email", model.UpsertKeyField);
    }

    [Fact]
    public async Task Create_ValidPostMapsInputCallsServiceOnceAndRedirects()
    {
        var pipeline = Pipeline();
        var ruleService = new RuleServiceStub();
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), ruleService);

        var result = await controller.Create(pipeline.Id, new ValidationRuleFormViewModel
        {
            Type = ValidationType.NumericRange, Field = "email", Minimum = "1", Maximum = "2", ErrorMessage = "Range error"
        }, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(new ValidationRuleInput(ValidationType.NumericRange, "email", "1", "2", "Range error"), ruleService.Input);
        Assert.Equal(1, ruleService.CallCount);
    }

    [Fact]
    public async Task Create_InvalidPostRedisplaysWithoutCallingService()
    {
        var pipeline = Pipeline();
        var ruleService = new RuleServiceStub();
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), ruleService);
        controller.ModelState.AddModelError(nameof(ValidationRuleFormViewModel.Field), "Required.");

        var result = await controller.Create(pipeline.Id, new ValidationRuleFormViewModel { Type = ValidationType.Required }, CancellationToken.None);

        var model = Assert.IsType<ValidationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(["email"], model.AvailableMappedFields);
        Assert.Equal(0, ruleService.CallCount);
    }

    [Fact]
    public async Task Edit_GetHydratesRangeValuesAndCurrentFormContext()
    {
        var rule = new ValidationRule
        {
            Id = Guid.NewGuid(), Type = ValidationType.NumericRange, Field = "email", ErrorMessage = "Range error",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "12,5", ["Maximum"] = "20,5" }
        };
        var pipeline = Pipeline();
        pipeline.ValidationRules = [rule];
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), new RuleServiceStub());

        var result = await controller.Edit(pipeline.Id, rule.Id, CancellationToken.None);

        var model = Assert.IsType<ValidationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(ValidationType.NumericRange, model.Type);
        Assert.Equal("email", model.Field);
        Assert.Equal("12,5", model.Minimum);
        Assert.Equal("20,5", model.Maximum);
        Assert.Equal("Range error", model.ErrorMessage);
        Assert.Equal("email", model.UpsertKeyField);
    }

    [Fact]
    public async Task Edit_GetUsesCurrentPipelineUpsertKeyAndReturnsNotFoundForMissingRule()
    {
        var rule = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.UpsertKeyRequired, Field = "oldKey" };
        var pipeline = Pipeline();
        pipeline.ValidationRules = [rule];
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), new RuleServiceStub());

        var result = await controller.Edit(pipeline.Id, rule.Id, CancellationToken.None);
        var model = Assert.IsType<ValidationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(model.Field);
        Assert.Equal("email", model.UpsertKeyField);
        Assert.IsType<NotFoundResult>(await controller.Edit(pipeline.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Edit_ValidPostMapsInputCallsServiceOnceAndRedirects()
    {
        var pipeline = Pipeline();
        var ruleService = new RuleServiceStub();
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), ruleService);
        var ruleId = Guid.NewGuid();

        var result = await controller.Edit(pipeline.Id, ruleId, new ValidationRuleFormViewModel
        {
            Type = ValidationType.DateRange, Field = "email", Minimum = "2026-01-01", Maximum = "2026-12-31", ErrorMessage = "Date error"
        }, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(ruleId, ruleService.RuleId);
        Assert.Equal(new ValidationRuleInput(ValidationType.DateRange, "email", "2026-01-01", "2026-12-31", "Date error"), ruleService.Input);
        Assert.Equal(1, ruleService.UpdateCallCount);
    }

    [Fact]
    public async Task Edit_InvalidPostRedisplaysWithoutCallingServiceAndMissingRuleReturnsNotFound()
    {
        var pipeline = Pipeline();
        var ruleService = new RuleServiceStub();
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), ruleService);
        controller.ModelState.AddModelError(nameof(ValidationRuleFormViewModel.Field), "Required.");

        var result = await controller.Edit(pipeline.Id, Guid.NewGuid(), new ValidationRuleFormViewModel { Type = ValidationType.Required }, CancellationToken.None);

        var model = Assert.IsType<ValidationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(["email"], model.AvailableMappedFields);
        Assert.Equal(0, ruleService.UpdateCallCount);

        ruleService.UpdateResult = false;
        var notFound = await new ValidationRulesController(new PipelineServiceStub(pipeline), ruleService).Edit(
            pipeline.Id, Guid.NewGuid(), new ValidationRuleFormViewModel { Type = ValidationType.Required, Field = "email" }, CancellationToken.None);
        Assert.IsType<NotFoundResult>(notFound);
    }

    [Fact]
    public async Task Edit_ServiceValidationFailureRedisplaysWithCurrentFieldOptions()
    {
        var pipeline = Pipeline();
        var ruleService = new RuleServiceStub { UpdateException = new ArgumentException("The field must be an included mapped output field.") };
        var controller = new ValidationRulesController(new PipelineServiceStub(pipeline), ruleService);
        var model = new ValidationRuleFormViewModel { Type = ValidationType.Required, Field = "stale", ErrorMessage = "Keep this" };

        var result = await controller.Edit(pipeline.Id, Guid.NewGuid(), model, CancellationToken.None);

        var redisplayed = Assert.IsType<ValidationRuleFormViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Same(model, redisplayed);
        Assert.Equal(["email"], redisplayed.AvailableMappedFields);
        Assert.Equal("stale", redisplayed.Field);
        Assert.Equal("Keep this", redisplayed.ErrorMessage);
        Assert.Equal(1, ruleService.UpdateCallCount);
        Assert.True(controller.ModelState.ContainsKey(string.Empty));
    }

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(), Name = "Import", UpsertKeyField = "email",
        FieldMappings =
        [
            new() { SourceField = "Email", TargetField = "email", IsIncluded = true },
            new() { SourceField = "Hidden", TargetField = "hidden", IsIncluded = false }
        ]
    };

    private sealed class RuleServiceStub : IValidationRuleService
    {
        public ValidationRuleInput? Input { get; private set; }
        public Guid RuleId { get; private set; }
        public int CallCount { get; private set; }
        public int UpdateCallCount { get; private set; }
        public bool UpdateResult { get; set; } = true;
        public Exception? UpdateException { get; set; }
        public Task<ValidationRule?> CreateAsync(Guid pipelineId, ValidationRuleInput input, CancellationToken cancellationToken)
        {
            CallCount++;
            Input = input;
            return Task.FromResult<ValidationRule?>(new ValidationRule());
        }
        public Task<bool> UpdateAsync(Guid pipelineId, Guid ruleId, ValidationRuleInput input, CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            RuleId = ruleId;
            Input = input;
            if (UpdateException is not null) return Task.FromException<bool>(UpdateException);
            return Task.FromResult(UpdateResult);
        }
    }

    private sealed class PipelineServiceStub(PipelineDefinition pipeline) : IPipelineService
    {
        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == pipeline.Id ? pipeline : null);
        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
