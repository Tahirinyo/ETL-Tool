using System.Globalization;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace EtlTool.UnitTests.Web.Models;

public sealed class TransformationRuleReorderViewModelBindingTests
{
    [Fact]
    public async Task BindAsync_RepeatedOrderedRuleIdsPreservesSubmittedOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var model = await BindAsync(new Dictionary<string, StringValues>
        {
            ["OrderedRuleIds"] = new StringValues([third.ToString(), first.ToString(), second.ToString()])
        });

        Assert.Equal([third, first, second], model.OrderedRuleIds);
    }

    private static async Task<TransformationRuleReorderViewModel> BindAsync(
        Dictionary<string, StringValues> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        await using var provider = services.BuildServiceProvider();
        var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
        var binderFactory = provider.GetRequiredService<IModelBinderFactory>();
        var metadata = metadataProvider.GetMetadataForType(typeof(TransformationRuleReorderViewModel));
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
        return Assert.IsType<TransformationRuleReorderViewModel>(bindingContext.Result.Model);
    }
}
