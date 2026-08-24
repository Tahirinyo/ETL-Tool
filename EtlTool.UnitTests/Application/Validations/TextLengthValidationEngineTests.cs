using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class TextLengthValidationEngineTests
{
    private readonly ValidationEngine _engine = new(
        new ValidationHandlerRegistry([
            new RequiredValidationHandler(),
            new EmailValidationHandler(),
            new NumericRangeValidationHandler(),
            new TextLengthValidationHandler()
        ]));

    [Fact]
    public void Validate_DispatchesTextLengthRuleThroughNormalValidationResult()
    {
        var rule = Rule(ValidationType.TextLengthRange, "Name");
        rule.Configuration["Maximum"] = "3";

        var result = _engine.Validate(Row(("Name", "Ada Lovelace")), [rule], Options());

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Name", error.Field);
        Assert.Equal("Field 'Name' must be at most 3 characters.", error.Message);
    }

    [Fact]
    public void Validate_ComposesRequiredAndTextLengthWithoutSharingResponsibilities()
    {
        var length = Rule(ValidationType.TextLengthRange, "Name");
        length.Configuration["Minimum"] = "3";
        var rules = new[]
        {
            Rule(ValidationType.Required, "Name"),
            length
        };

        var missing = _engine.Validate(Row(("Name", " ")), rules, Options());
        var shortText = _engine.Validate(Row(("Name", "Al")), rules, Options());
        var valid = _engine.Validate(Row(("Name", "Ada")), rules, Options());

        Assert.Equal("Field 'Name' is required.", Assert.Single(missing.Errors).Message);
        Assert.Equal("Field 'Name' must be at least 3 characters.", Assert.Single(shortText.Errors).Message);
        Assert.True(valid.IsValid);
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

    private static SourceOptions Options() => new();
}
