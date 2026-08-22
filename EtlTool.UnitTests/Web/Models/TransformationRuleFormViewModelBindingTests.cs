using System.ComponentModel.DataAnnotations;
using System.Globalization;
using EtlTool.Domain.Enums;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace EtlTool.UnitTests.Web.Models;

public sealed class TransformationRuleFormViewModelBindingTests
{
    [Theory]
    [InlineData(TransformationType.Trim)]
    [InlineData(TransformationType.ToUpper)]
    [InlineData(TransformationType.ToLower)]
    [InlineData(TransformationType.ConvertToString)]
    [InlineData(TransformationType.ConvertToInteger)]
    [InlineData(TransformationType.ConvertToDecimal)]
    [InlineData(TransformationType.ConvertToDate)]
    public void Validate_AcceptsFieldOnlyTransformationTypes(TransformationType type)
    {
        var results = Validate(new TransformationRuleFormViewModel { Type = type, SourceField = "name" });

        Assert.Empty(results);
    }

    [Fact]
    public void Validate_RequiresFilterOperatorAndComparisonValue()
    {
        var results = Validate(new TransformationRuleFormViewModel
        {
            Type = TransformationType.FilterRow,
            SourceField = "amount"
        });

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(TransformationRuleFormViewModel.FilterOperator)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(TransformationRuleFormViewModel.FilterValue)));
    }

    [Fact]
    public void Validate_AcceptsDeduplicationWithoutSourceFieldWhenFieldsSelected()
    {
        var results = Validate(new TransformationRuleFormViewModel
        {
            Type = TransformationType.Deduplicate,
            SelectedFields = ["email", "company"]
        });

        Assert.Empty(results);
    }

    [Fact]
    public void Validate_RequiresDeduplicationFieldSelection()
    {
        var results = Validate(new TransformationRuleFormViewModel { Type = TransformationType.Deduplicate });

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(TransformationRuleFormViewModel.SelectedFields)));
    }

    [Theory]
    [InlineData("DefaultValue")]
    [InlineData("Find")]
    [InlineData("Replace")]
    public async Task BindAsync_ExplicitEmptyStringRemainsEmpty(string propertyName)
    {
        var model = await BindAsync(new Dictionary<string, StringValues>
        {
            ["Type"] = TransformationType.FindAndReplace.ToString(),
            ["SourceField"] = "name",
            ["DefaultValue"] = "default",
            ["Find"] = "find",
            ["Replace"] = "replace",
            [propertyName] = ""
        });

        Assert.Equal("", propertyName switch
        {
            "DefaultValue" => model.DefaultValue,
            "Find" => model.Find,
            _ => model.Replace
        });
    }

    [Theory]
    [InlineData(TransformationType.SetDefaultValue, "DefaultValue")]
    [InlineData(TransformationType.FindAndReplace, "Replace")]
    public async Task BindAsync_OmittedRequiredConfigurationRemainsNullAndFailsConditionalValidation(
        TransformationType type,
        string omittedProperty)
    {
        var values = new Dictionary<string, StringValues>
        {
            ["Type"] = type.ToString(),
            ["SourceField"] = "name"
        };
        if (type == TransformationType.FindAndReplace) values["Find"] = "find";

        var model = await BindAsync(values);
        Assert.Null(omittedProperty == "DefaultValue" ? model.DefaultValue : model.Replace);
        Assert.Contains(Validate(model), result => result.MemberNames.Contains(omittedProperty));
    }

    [Theory]
    [InlineData("DefaultValue")]
    [InlineData("Find")]
    [InlineData("Replace")]
    public async Task BindAsync_WhitespaceIsNotNormalized(string propertyName)
    {
        var model = await BindAsync(new Dictionary<string, StringValues>
        {
            ["Type"] = TransformationType.FindAndReplace.ToString(),
            ["SourceField"] = "name",
            ["DefaultValue"] = "default",
            ["Find"] = "find",
            ["Replace"] = "replace",
            [propertyName] = "  "
        });

        Assert.Equal("  ", propertyName switch
        {
            "DefaultValue" => model.DefaultValue,
            "Find" => model.Find,
            _ => model.Replace
        });
    }

    private static async Task<TransformationRuleFormViewModel> BindAsync(
        Dictionary<string, StringValues> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        await using var provider = services.BuildServiceProvider();
        var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
        var binderFactory = provider.GetRequiredService<IModelBinderFactory>();
        var metadata = metadataProvider.GetMetadataForType(typeof(TransformationRuleFormViewModel));
        var bindingContext = DefaultModelBindingContext.CreateBindingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary()),
            new FormValueProvider(BindingSource.Form, new FormCollection(values), CultureInfo.InvariantCulture),
            metadata,
            bindingInfo: null,
            modelName: string.Empty);
        var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
        {
            BindingInfo = new BindingInfo { BindingSource = BindingSource.Form },
            Metadata = metadata,
            CacheToken = metadata
        });

        await binder.BindModelAsync(bindingContext);
        return Assert.IsType<TransformationRuleFormViewModel>(bindingContext.Result.Model);
    }

    private static IReadOnlyList<ValidationResult> Validate(TransformationRuleFormViewModel model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }
}
