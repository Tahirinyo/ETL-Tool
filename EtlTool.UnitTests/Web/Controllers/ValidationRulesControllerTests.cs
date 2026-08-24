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
        public int CallCount { get; private set; }
        public Task<ValidationRule?> CreateAsync(Guid pipelineId, ValidationRuleInput input, CancellationToken cancellationToken)
        {
            CallCount++;
            Input = input;
            return Task.FromResult<ValidationRule?>(new ValidationRule());
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
