using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class UpsertKeyValidationEngineTests
{
    private readonly ValidationEngine _engine = new(
        new ValidationHandlerRegistry([
            new RequiredValidationHandler(),
            new EmailValidationHandler(),
            new NumericRangeValidationHandler(),
            new UpsertKeyValidationHandler()
        ]));

    [Fact]
    public void Validate_DispatchesUpsertKeyRuleThroughNormalValidationResult()
    {
        var result = _engine.Validate(
            Row(("CustomerId", " \t ")),
            [Rule(ValidationType.UpsertKeyRequired, "CustomerId")],
            Options());

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("CustomerId", error.Field);
        Assert.Equal("Upsert key field 'CustomerId' is required.", error.Message);
    }

    [Fact]
    public void Validate_StopsAfterUpsertKeyFailure()
    {
        var laterHandler = new TrackingHandler(ValidationType.EmailFormat);
        var engine = new ValidationEngine(new ValidationHandlerRegistry([
            new UpsertKeyValidationHandler(),
            laterHandler
        ]));

        var result = engine.Validate(
            Row(("CustomerId", null), ("Email", "not-an-email")),
            [
                Rule(ValidationType.UpsertKeyRequired, "CustomerId"),
                Rule(ValidationType.EmailFormat, "Email")
            ],
            Options());

        Assert.False(result.IsValid);
        Assert.Equal("CustomerId", Assert.Single(result.Errors).Field);
        Assert.Equal(0, laterHandler.InvocationCount);
    }

    [Fact]
    public void Validate_AllowsEarlierRequiredRuleToReachUpsertKey()
    {
        var result = _engine.Validate(
            Row(("CustomerId", "customer-42")),
            [
                Rule(ValidationType.Required, "CustomerId"),
                Rule(ValidationType.UpsertKeyRequired, "CustomerId")
            ],
            Options());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_StopsBeforeUpsertKeyWhenEarlierRuleFails()
    {
        var upsertKeyHandler = new TrackingHandler(ValidationType.UpsertKeyRequired);
        var engine = new ValidationEngine(new ValidationHandlerRegistry([
            new RequiredValidationHandler(),
            upsertKeyHandler
        ]));

        var result = engine.Validate(
            Row(("CustomerId", " \t ")),
            [
                Rule(ValidationType.Required, "CustomerId"),
                Rule(ValidationType.UpsertKeyRequired, "CustomerId")
            ],
            Options());

        Assert.False(result.IsValid);
        Assert.Equal("CustomerId", Assert.Single(result.Errors).Field);
        Assert.Equal(0, upsertKeyHandler.InvocationCount);
    }

    [Fact]
    public void Validate_InvokesUpsertKeyExactlyOnceAfterEarlierSuccess()
    {
        var upsertKeyHandler = new TrackingHandler(ValidationType.UpsertKeyRequired);
        var engine = new ValidationEngine(new ValidationHandlerRegistry([
            new RequiredValidationHandler(),
            upsertKeyHandler
        ]));

        var result = engine.Validate(
            Row(("CustomerId", "customer-42")),
            [
                Rule(ValidationType.Required, "CustomerId"),
                Rule(ValidationType.UpsertKeyRequired, "CustomerId")
            ],
            Options());

        Assert.True(result.IsValid);
        Assert.Equal(1, upsertKeyHandler.InvocationCount);
    }

    private static ValidationRule Rule(ValidationType type, string field) => new()
    {
        Type = type,
        Field = field
    };

    private static DataRow Row(params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = 2 };

        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }

    private static SourceOptions Options(string? cultureName = null) => new()
    {
        CultureName = cultureName ?? string.Empty
    };

    private sealed class TrackingHandler(ValidationType type) : IValidationHandler
    {
        public ValidationType Type { get; } = type;

        public int InvocationCount { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule)
        {
            InvocationCount++;
            return ValidationResult.Valid(row);
        }
    }
}
