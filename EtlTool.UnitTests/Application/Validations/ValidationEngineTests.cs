using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class ValidationEngineTests
{
    private readonly ValidationEngine _engine = new(
        new ValidationHandlerRegistry([
            new RequiredValidationHandler(),
            new EmailValidationHandler(),
            new NumericRangeValidationHandler()
        ]));

    [Fact]
    public void Validate_ReturnsValidForRowThatPassesRequiredRule()
    {
        var row = Row(("Name", "Ada"));

        var result = _engine.Validate(row, [Rule(ValidationType.Required, "Name")], Options());

        Assert.True(result.IsValid);
        Assert.Same(row, result.Row);
    }

    [Fact]
    public void Validate_ReturnsFirstFailureWithoutEvaluatingLaterRules()
    {
        var row = Row(("Name", null));

        var result = _engine.Validate(row, [Rule(ValidationType.Required, "Name"), new ValidationRule
        {
            Type = ValidationType.EmailFormat,
            Field = "Email"
        }], Options());

        Assert.False(result.IsValid);
        Assert.Equal("Name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_DispatchesEmailRule()
    {
        var row = Row(("Email", "not-an-email"));

        var result = _engine.Validate(row, [Rule(ValidationType.EmailFormat, "Email")], Options());

        Assert.False(result.IsValid);
        Assert.Equal("Email", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_EmailRuleDoesNotDuplicateRequiredPresenceValidation()
    {
        var row = Row(("Email", " \t "));

        var result = _engine.Validate(row, [
            Rule(ValidationType.EmailFormat, "Email"),
            Rule(ValidationType.Required, "Email")
        ], Options());

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Email", error.Field);
        Assert.Equal("Field 'Email' is required.", error.Message);
    }

    [Fact]
    public void Validate_DispatchesNumericRangeWithPipelineSourceCulture()
    {
        var rule = Rule(ValidationType.NumericRange, "Amount");
        rule.Configuration["Minimum"] = "1,5";
        rule.Configuration["Maximum"] = "2,5";

        var result = _engine.Validate(
            Row(("Amount", 1.5m)),
            [rule],
            Options("tr-TR"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ReturnsNumericRangeFailureThroughNormalValidationResult()
    {
        var rule = Rule(ValidationType.NumericRange, "Amount");
        rule.Configuration["Maximum"] = "10";

        var result = _engine.Validate(
            Row(("Amount", 11L)),
            [rule],
            Options());

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Amount", error.Field);
        Assert.Equal("Field 'Amount' must be at most 10.", error.Message);
    }

    [Fact]
    public void Validate_ThrowsForNullArgumentsAndInvalidRuleCollectionEntry()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.Validate(null!, [], Options()));
        Assert.Throws<ArgumentNullException>(() => _engine.Validate(Row(), null!, Options()));
        Assert.Throws<ArgumentNullException>(() => _engine.Validate(Row(), [], null!));
        Assert.Throws<ArgumentException>(() => _engine.Validate(Row(), [null!], Options()));
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
}
