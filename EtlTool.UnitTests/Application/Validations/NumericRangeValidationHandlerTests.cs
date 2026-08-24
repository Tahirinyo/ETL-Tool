using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class NumericRangeValidationHandlerTests
{
    private readonly NumericRangeValidationHandler _handler = new();

    [Theory]
    [InlineData(15L)]
    [InlineData(17L)]
    [InlineData(20L)]
    public void Validate_PassesLongValueWithinInclusiveBounds(long value)
    {
        var row = Row(12, ("Amount", value));

        var result = _handler.Validate(row, Rule("Amount", minimum: "15", maximum: "20"), InvariantCulture);

        Assert.True(result.IsValid);
        Assert.Same(row, result.Row);
    }

    [Theory]
    [InlineData(14L)]
    [InlineData(21L)]
    public void Validate_FailsLongValueOutsideInclusiveBounds(long value)
    {
        var row = Row(5, ("Amount", value));

        var result = _handler.Validate(row, Rule("Amount", minimum: "15", maximum: "20"), InvariantCulture);

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Amount", error.Field);
        Assert.Equal("Field 'Amount' must be between 15 and 20.", error.Message);
        Assert.Equal(value, row.Values["Amount"]);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.75)]
    public void Validate_PassesDecimalValueWithinInclusiveBounds(double value)
    {
        var row = Row(8, ("Amount", (decimal)value));

        var result = _handler.Validate(row, Rule("Amount", minimum: "1.5", maximum: "2.75"), InvariantCulture);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AppliesMinimumOnlyAndMaximumOnlyRules()
    {
        Assert.True(_handler.Validate(
            Row(3, ("Amount", 10L)),
            Rule("Amount", minimum: "10"),
            InvariantCulture).IsValid);
        Assert.False(_handler.Validate(
            Row(3, ("Amount", 9L)),
            Rule("Amount", minimum: "10"),
            InvariantCulture).IsValid);
        Assert.True(_handler.Validate(
            Row(3, ("Amount", 10m)),
            Rule("Amount", maximum: "10"),
            InvariantCulture).IsValid);
        Assert.False(_handler.Validate(
            Row(3, ("Amount", 10.1m)),
            Rule("Amount", maximum: "10"),
            InvariantCulture).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Validate_PassesOptionalNullOrBlankValue(object? value)
    {
        var result = _handler.Validate(
            Row(7, ("Amount", value)),
            Rule("Amount", minimum: "10"),
            InvariantCulture);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PassesWhenConfiguredFieldIsAbsent()
    {
        var result = _handler.Validate(
            Row(7, ("Other", 10L)),
            Rule("Amount", minimum: "10"),
            InvariantCulture);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("10")]
    [InlineData(10.0)]
    [InlineData(true)]
    public void Validate_FailsPresentUnsupportedValueWithoutCoercion(object value)
    {
        var row = Row(9, ("Amount", value), ("Other", 42L));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("Amount", minimum: "10"), InvariantCulture);

        Assert.False(result.IsValid);
        Assert.Equal("Amount", Assert.Single(result.Errors).Field);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Fact]
    public void Validate_UsesConfiguredCustomErrorMessage()
    {
        var result = _handler.Validate(
            Row(10, ("Amount", 4L)),
            Rule("Amount", "Amount is out of range.", minimum: "5"),
            InvariantCulture);

        Assert.Equal("Amount is out of range.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_DoesNotMutateTypedNumericRow()
    {
        var row = Row(10, ("Amount", 12.5m), ("Other", 42L));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(
            row,
            Rule("Amount", minimum: "10", maximum: "15"),
            InvariantCulture);

        Assert.True(result.IsValid);
        Assert.Same(row, result.Row);
        Assert.Equal(10, row.SourceRowNumber);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Fact]
    public void Validate_ParsesBoundsUsingProvidedSourceCulture()
    {
        var turkish = _handler.Validate(
            Row(11, ("Amount", 1.5m)),
            Rule("Amount", minimum: "1,5", maximum: "2,5"),
            CultureInfo.GetCultureInfo("tr-TR"));
        var us = _handler.Validate(
            Row(11, ("Amount", 1.5m)),
            Rule("Amount", minimum: "1.5", maximum: "2.5"),
            CultureInfo.GetCultureInfo("en-US"));

        Assert.True(turkish.IsValid);
        Assert.True(us.IsValid);
    }

    [Fact]
    public void Validate_DoesNotUseCurrentCultureForBoundParsing()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            var result = _handler.Validate(
                Row(12, ("Amount", 1234.5m)),
                Rule("Amount", minimum: "1,234.5", maximum: "1,234.5"),
                InvariantCulture);

            Assert.True(result.IsValid);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(null, " ")]
    [InlineData("not-a-number", null)]
    [InlineData("10", "9")]
    public void Validate_RejectsInvalidRangeConfiguration(string? minimum, string? maximum)
    {
        var rule = Rule("Amount", minimum: minimum, maximum: maximum);

        var exception = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(13, ("Amount", 15L)),
            rule,
            InvariantCulture));

        Assert.Contains("numeric range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsCaseMismatchedConfigurationKey()
    {
        var rule = Rule("Amount");
        rule.Configuration["minimum"] = "10";

        Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(14, ("Amount", 15L)),
            rule,
            InvariantCulture));
    }

    [Fact]
    public void Validate_RejectsInvalidFieldBeforeReadingRowValue()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(15, ("Amount", 15L)),
            Rule(" ", minimum: "10"),
            InvariantCulture));

        Assert.Contains("field", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ThrowsWhenCalledWithoutSourceCulture()
    {
        Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(16, ("Amount", 15L)),
            Rule("Amount", minimum: "10")));
    }

    [Fact]
    public void Type_IsNumericRange()
    {
        Assert.Equal(ValidationType.NumericRange, _handler.Type);
    }

    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    private static ValidationRule Rule(
        string field,
        string errorMessage = "",
        string? minimum = null,
        string? maximum = null)
    {
        var rule = new ValidationRule
        {
            Type = ValidationType.NumericRange,
            Field = field,
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
