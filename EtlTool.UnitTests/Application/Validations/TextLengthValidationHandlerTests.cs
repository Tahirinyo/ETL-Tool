using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class TextLengthValidationHandlerTests
{
    private readonly TextLengthValidationHandler _handler = new();

    [Theory]
    [InlineData("Ada")]
    [InlineData("Ada Lovelace")]
    [InlineData("Ada Lovelace Byron")]
    public void Validate_PassesTextWithinInclusiveBounds(string value)
    {
        var row = Row(12, ("Name", value));

        var result = _handler.Validate(row, Rule("Name", minimum: "3", maximum: "18"));

        Assert.True(result.IsValid);
        Assert.Same(row, result.Row);
    }

    [Theory]
    [InlineData("Al", "Field 'Name' must be between 3 and 5 characters.")]
    [InlineData("Adaline", "Field 'Name' must be between 3 and 5 characters.")]
    public void Validate_FailsTextOutsideInclusiveBounds(string value, string expectedMessage)
    {
        var row = Row(5, ("Name", value));

        var result = _handler.Validate(row, Rule("Name", minimum: "3", maximum: "5"));

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Name", error.Field);
        Assert.Equal(expectedMessage, error.Message);
        Assert.Equal(value, row.Values["Name"]);
    }

    [Fact]
    public void Validate_AppliesMinimumOnlyAndMaximumOnlyRules()
    {
        Assert.True(_handler.Validate(Row(3, ("Name", "Ada")), Rule("Name", minimum: "3")).IsValid);
        Assert.True(_handler.Validate(Row(3, ("Name", "Adaline")), Rule("Name", minimum: "3")).IsValid);
        Assert.False(_handler.Validate(Row(3, ("Name", "Al")), Rule("Name", minimum: "3")).IsValid);
        Assert.True(_handler.Validate(Row(3, ("Name", "Al")), Rule("Name", maximum: "3")).IsValid);
        Assert.True(_handler.Validate(Row(3, ("Name", "Ada")), Rule("Name", maximum: "3")).IsValid);
        Assert.False(_handler.Validate(Row(3, ("Name", "Adaline")), Rule("Name", maximum: "3")).IsValid);
    }

    [Fact]
    public void Validate_UsesStoredWhitespaceInNonblankTextWithoutMutation()
    {
        var row = Row(4, ("Name", " Ada "), ("Other", 42L));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("Name", minimum: "5", maximum: "5"));

        Assert.True(result.IsValid);
        Assert.Same(row, result.Row);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Validate_PassesOptionalNullOrBlankValue(string? value)
    {
        var result = _handler.Validate(Row(7, ("Name", value)), Rule("Name", minimum: "1"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PassesWhenConfiguredFieldIsAbsent()
    {
        Assert.True(_handler.Validate(Row(7, ("Other", "Ada")), Rule("Name", minimum: "1")).IsValid);
    }

    [Fact]
    public void Validate_FailsRepresentativePresentNonStringValuesWithoutCoercion()
    {
        foreach (object value in new object[] { 42L, 12.5m, true })
        {
            var row = Row(9, ("Name", value), ("Other", 42L));
            var originalValues = row.Values.ToArray();

            var result = _handler.Validate(row, Rule("Name", minimum: "1"));

            Assert.False(result.IsValid);
            Assert.Equal("Name", Assert.Single(result.Errors).Field);
            Assert.Equal(originalValues, row.Values.ToArray());
        }
    }

    [Fact]
    public void Validate_UsesOrdinalConfiguredFieldLookup()
    {
        var result = _handler.Validate(Row(9, ("Name", "Al")), Rule("name", minimum: "3"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UsesConfiguredCustomErrorMessage()
    {
        var result = _handler.Validate(
            Row(10, ("Name", "Al")),
            Rule("Name", "Name is too short.", minimum: "3"));

        Assert.Equal("Name is too short.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_AfterTrim_UsesTransformedValueWithoutFurtherMutation()
    {
        var row = Row(15, ("Name", "  Ada  "));
        new TrimTransformationHandler().Apply(row, new TransformationRule
        {
            Type = TransformationType.Trim,
            SourceField = "Name"
        });
        var transformedValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("Name", maximum: "3"));

        Assert.True(result.IsValid);
        Assert.Equal(transformedValues, row.Values.ToArray());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(null, " ")]
    [InlineData("not-a-number", null)]
    [InlineData(null, "not-a-number")]
    [InlineData("-1", null)]
    [InlineData(null, "-1")]
    [InlineData("2147483648", null)]
    [InlineData("5", "4")]
    public void Validate_RejectsInvalidRangeConfiguration(string? minimum, string? maximum)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(13, ("Name", "Ada")), Rule("Name", minimum: minimum, maximum: maximum)));

        Assert.Contains("text length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsCaseMismatchedConfigurationKey()
    {
        var rule = Rule("Name");
        rule.Configuration["minimum"] = "3";

        Assert.Throws<InvalidOperationException>(() => _handler.Validate(Row(14, ("Name", "Ada")), rule));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Validate_ThrowsWhenRuleFieldIsInvalid(string? field)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Validate(Row(15, ("Name", "Ada")), Rule(field, minimum: "1")));

        Assert.Contains("field", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(null!, Rule("Name", minimum: "1")));
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(Row(16), null!));
    }

    [Fact]
    public void Type_IsTextLengthRange()
    {
        Assert.Equal(ValidationType.TextLengthRange, _handler.Type);
    }

    private static ValidationRule Rule(
        string? field,
        string errorMessage = "",
        string? minimum = null,
        string? maximum = null)
    {
        var rule = new ValidationRule
        {
            Type = ValidationType.TextLengthRange,
            Field = field!,
            ErrorMessage = errorMessage
        };

        if (minimum is not null)
        {
            rule.Configuration["Minimum"] = minimum;
        }

        if (maximum is not null)
        {
            rule.Configuration["Maximum"] = maximum;
        }

        return rule;
    }

    private static DataRow Row(long sourceRowNumber, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };

        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }
}
