using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class EmailValidationHandlerTests
{
    private readonly EmailValidationHandler _handler = new();

    [Theory]
    [InlineData("ada@example.com")]
    [InlineData("user.name+tag@example.org")]
    public void Validate_PassesRepresentativeValidEmail(string value)
    {
        var row = Row(12, ("Email", value));

        var result = _handler.Validate(row, Rule("Email"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Same(row, result.Row);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("ada@")]
    public void Validate_FailsMalformedEmail(string value)
    {
        var row = Row(5, ("Email", value));

        var result = _handler.Validate(row, Rule("Email"));

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Email", error.Field);
        Assert.Equal("Field 'Email' must be a valid email address.", error.Message);
        Assert.Equal(value, row.Values["Email"]);
    }

    [Fact]
    public void Validate_PassesWhenConfiguredFieldIsAbsent()
    {
        var result = _handler.Validate(Row(6, ("Name", "Ada")), Rule("Email"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Validate_PassesWhenStringValueIsNotPresent(string? value)
    {
        var result = _handler.Validate(Row(7, ("Email", value)), Rule("Email"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(42L)]
    [InlineData(true)]
    public void Validate_FailsNonStringValueWithoutCoercion(object value)
    {
        var row = Row(8, ("Email", value));

        var result = _handler.Validate(row, Rule("Email"));

        Assert.False(result.IsValid);
        Assert.Equal("Email", Assert.Single(result.Errors).Field);
        Assert.Equal(value, row.Values["Email"]);
    }

    [Fact]
    public void Validate_UsesOrdinalConfiguredFieldLookup()
    {
        var result = _handler.Validate(Row(9, ("Email", "invalid")), Rule("email"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_FailsConfiguredMalformedFieldDespiteValidUnrelatedField()
    {
        var result = _handler.Validate(
            Row(9, ("Email", "invalid"), ("AlternateEmail", "ada@example.com")),
            Rule("Email"));

        Assert.False(result.IsValid);
        Assert.Equal("Email", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_UsesConfiguredCustomErrorMessage()
    {
        var result = _handler.Validate(
            Row(10, ("Email", "invalid")),
            Rule("Email", "Email address is invalid."));

        Assert.Equal("Email address is invalid.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_DoesNotMutateRow()
    {
        var row = Row(27, ("Email", "Ada@Example.COM"), ("Other", 42L), ("Missing", null));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("Email"));

        Assert.True(result.IsValid);
        Assert.Equal(27, row.SourceRowNumber);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Fact]
    public void Validate_AfterTrim_UsesTransformedValueWithoutFurtherMutation()
    {
        var row = Row(15, ("Email", "  ada@example.com  "));
        new TrimTransformationHandler().Apply(row, new TransformationRule
        {
            Type = TransformationType.Trim,
            SourceField = "Email"
        });
        var transformedValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("Email"));

        Assert.True(result.IsValid);
        Assert.Equal(transformedValues, row.Values.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Validate_ThrowsWhenRuleFieldIsInvalid(string? field)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Validate(Row(11, ("Email", "ada@example.com")), Rule(field)));

        Assert.Contains("field", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(null!, Rule("Email")));
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(Row(13), null!));
    }

    [Fact]
    public void Type_IsEmailFormat()
    {
        Assert.Equal(ValidationType.EmailFormat, _handler.Type);
    }

    private static ValidationRule Rule(string? field, string errorMessage = "") => new()
    {
        Type = ValidationType.EmailFormat,
        Field = field!,
        ErrorMessage = errorMessage
    };

    private static DataRow Row(
        long sourceRowNumber,
        params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };

        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }
}
