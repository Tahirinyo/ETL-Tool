using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class ValidationEngineTests
{
    private readonly ValidationEngine _engine = new(
        new ValidationHandlerRegistry([
            new RequiredValidationHandler(),
            new EmailValidationHandler()
        ]));

    [Fact]
    public void Validate_ReturnsValidForRowThatPassesRequiredRule()
    {
        var row = Row(("Name", "Ada"));

        var result = _engine.Validate(row, [Rule(ValidationType.Required, "Name")]);

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
        }]);

        Assert.False(result.IsValid);
        Assert.Equal("Name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_DispatchesEmailRule()
    {
        var row = Row(("Email", "not-an-email"));

        var result = _engine.Validate(row, [Rule(ValidationType.EmailFormat, "Email")]);

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
        ]);

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Email", error.Field);
        Assert.Equal("Field 'Email' is required.", error.Message);
    }

    [Fact]
    public void Validate_ThrowsForNullArgumentsAndInvalidRuleCollectionEntry()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.Validate(null!, []));
        Assert.Throws<ArgumentNullException>(() => _engine.Validate(Row(), null!));
        Assert.Throws<ArgumentException>(() => _engine.Validate(Row(), [null!]));
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
}
