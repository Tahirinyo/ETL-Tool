using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class ValidationHandlerRegistryTests
{
    [Fact]
    public void Resolve_ReturnsHandlerRegisteredForExactType()
    {
        var required = new StubHandler(ValidationType.Required);
        var email = new StubHandler(ValidationType.EmailFormat);
        var registry = new ValidationHandlerRegistry([required, email]);

        Assert.Same(required, registry.Resolve(ValidationType.Required));
        Assert.Same(email, registry.Resolve(ValidationType.EmailFormat));
    }

    [Fact]
    public void Constructor_RejectsDuplicateHandlerTypes()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ValidationHandlerRegistry([
                new StubHandler(ValidationType.Required),
                new StubHandler(ValidationType.Required)
            ]));

        Assert.Contains(nameof(ValidationType.Required), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullCollectionAndEntry()
    {
        Assert.Throws<ArgumentNullException>(() => new ValidationHandlerRegistry(null!));
        Assert.Throws<ArgumentException>(() => new ValidationHandlerRegistry([null!]));
    }

    [Theory]
    [InlineData(ValidationType.EmailFormat, "EmailFormat")]
    [InlineData((ValidationType)999, "999")]
    public void Resolve_ThrowsWhenTypeHasNoRegisteredHandler(
        ValidationType type,
        string expectedType)
    {
        var exception = Assert.Throws<KeyNotFoundException>(() =>
            new ValidationHandlerRegistry([]).Resolve(type));

        Assert.Contains(expectedType, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composition_ResolvesConcreteRequiredHandlerThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IValidationHandler, RequiredValidationHandler>();
        services.AddSingleton<ValidationHandlerRegistry>();
        services.AddSingleton<ValidationEngine>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<RequiredValidationHandler>(provider
            .GetRequiredService<ValidationHandlerRegistry>()
            .Resolve(ValidationType.Required));
        Assert.IsType<ValidationEngine>(provider.GetRequiredService<ValidationEngine>());
    }

    private sealed class StubHandler(ValidationType type) : IValidationHandler
    {
        public ValidationType Type { get; } = type;

        public ValidationResult Validate(DataRow row, ValidationRule rule) =>
            ValidationResult.Valid(row);
    }
}
