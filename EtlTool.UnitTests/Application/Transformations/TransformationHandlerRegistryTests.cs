using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class TransformationHandlerRegistryTests
{
    [Fact]
    public void Resolve_ReturnsHandlerRegisteredForExactType()
    {
        var trim = new StubHandler(TransformationType.Trim);
        var toLower = new StubHandler(TransformationType.ToLower);
        var registry = new TransformationHandlerRegistry([trim, toLower]);

        Assert.Same(trim, registry.Resolve(TransformationType.Trim));
        Assert.Same(toLower, registry.Resolve(TransformationType.ToLower));
    }

    [Fact]
    public void Constructor_RejectsDuplicateHandlerTypes()
    {
        var first = new StubHandler(TransformationType.Trim);
        var second = new StubHandler(TransformationType.Trim);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new TransformationHandlerRegistry([first, second]));

        Assert.Contains(nameof(TransformationType.Trim), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullCollectionAndEntry()
    {
        Assert.Throws<ArgumentNullException>(
            () => new TransformationHandlerRegistry(null!));
        Assert.Throws<ArgumentException>(
            () => new TransformationHandlerRegistry([null!]));
    }

    [Theory]
    [InlineData(TransformationType.FindAndReplace, "FindAndReplace")]
    [InlineData((TransformationType)999, "999")]
    public void Resolve_ThrowsWhenTypeHasNoRegisteredHandler(
        TransformationType type,
        string expectedType)
    {
        var registry = new TransformationHandlerRegistry([]);

        var exception = Assert.Throws<KeyNotFoundException>(
            () => registry.Resolve(type));

        Assert.Contains(expectedType, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composition_ResolvesRegistryAndEngineWithoutConcreteHandlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TransformationHandlerRegistry>();
        services.AddSingleton<TransformationEngine>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var registry = provider.GetRequiredService<TransformationHandlerRegistry>();
        var engine = provider.GetRequiredService<TransformationEngine>();

        Assert.IsType<TransformationHandlerRegistry>(registry);
        Assert.IsType<TransformationEngine>(engine);
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve(TransformationType.Trim));
    }

    [Fact]
    public void Composition_ResolvesConcreteTrimHandlerThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransformationHandler, TrimTransformationHandler>();
        services.AddSingleton<TransformationHandlerRegistry>();
        services.AddSingleton<TransformationEngine>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var handler = provider.GetRequiredService<TransformationHandlerRegistry>()
            .Resolve(TransformationType.Trim);

        Assert.IsType<TrimTransformationHandler>(handler);
    }

    [Fact]
    public void Composition_ResolvesConcreteCaseHandlersThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransformationHandler, ToUpperTransformationHandler>();
        services.AddSingleton<ITransformationHandler, ToLowerTransformationHandler>();
        services.AddSingleton<TransformationHandlerRegistry>();
        services.AddSingleton<TransformationEngine>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var registry = provider.GetRequiredService<TransformationHandlerRegistry>();

        Assert.IsType<ToUpperTransformationHandler>(registry.Resolve(TransformationType.ToUpper));
        Assert.IsType<ToLowerTransformationHandler>(registry.Resolve(TransformationType.ToLower));
    }

    [Fact]
    public void Composition_ResolvesConcreteDefaultValueHandlerThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransformationHandler, DefaultValueTransformationHandler>();
        services.AddSingleton<TransformationHandlerRegistry>();
        services.AddSingleton<TransformationEngine>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var handler = provider.GetRequiredService<TransformationHandlerRegistry>()
            .Resolve(TransformationType.SetDefaultValue);

        Assert.IsType<DefaultValueTransformationHandler>(handler);
    }

    [Fact]
    public void Composition_ResolvesConcreteFindAndReplaceHandlerThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransformationHandler, FindAndReplaceTransformationHandler>();
        services.AddSingleton<TransformationHandlerRegistry>();
        services.AddSingleton<TransformationEngine>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var handler = provider.GetRequiredService<TransformationHandlerRegistry>()
            .Resolve(TransformationType.FindAndReplace);

        Assert.IsType<FindAndReplaceTransformationHandler>(handler);
    }

    [Fact]
    public void Composition_ResolvesConcreteNumericHandlersThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransformationHandler, ConvertToIntegerTransformationHandler>();
        services.AddSingleton<ITransformationHandler, ConvertToDecimalTransformationHandler>();
        services.AddSingleton<TransformationHandlerRegistry>();
        services.AddSingleton<TransformationEngine>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var registry = provider.GetRequiredService<TransformationHandlerRegistry>();

        Assert.IsType<ConvertToIntegerTransformationHandler>(
            registry.Resolve(TransformationType.ConvertToInteger));
        Assert.IsType<ConvertToDecimalTransformationHandler>(
            registry.Resolve(TransformationType.ConvertToDecimal));
    }

    private sealed class StubHandler(TransformationType type) : ITransformationHandler
    {
        public TransformationType Type { get; } = type;

        public DataRow Apply(DataRow row, TransformationRule rule) => row;
    }
}
