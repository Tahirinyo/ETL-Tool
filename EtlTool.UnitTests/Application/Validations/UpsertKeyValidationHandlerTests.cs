using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class UpsertKeyValidationHandlerTests
{
    private readonly UpsertKeyValidationHandler _handler = new();

    [Theory]
    [InlineData("customer-42")]
    [InlineData("  customer-42  ")]
    public void Validate_PassesWhenStringValueIsPresent(string value)
    {
        var row = Row(12, ("CustomerId", value));

        var result = _handler.Validate(row, Rule("CustomerId"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Same(row, result.Row);
        Assert.Equal(value, row.Values["CustomerId"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Validate_FailsWhenStringValueIsMissingOrBlank(string? value)
    {
        var row = Row(5, ("CustomerId", value));

        var result = _handler.Validate(row, Rule("CustomerId"));

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("CustomerId", error.Field);
        Assert.Equal("Upsert key field 'CustomerId' is required.", error.Message);
        Assert.Equal(value, row.Values["CustomerId"]);
    }

    [Fact]
    public void Validate_FailsWhenConfiguredFieldIsAbsent()
    {
        var row = Row(6, ("CustomerId", "customer-42"));

        var result = _handler.Validate(row, Rule("ExternalId"));

        Assert.False(result.IsValid);
        Assert.Equal("ExternalId", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_UsesOrdinalFieldLookup()
    {
        var result = _handler.Validate(
            Row(7, ("CustomerId", "customer-42")),
            Rule("customerId"));

        Assert.False(result.IsValid);
        Assert.Equal("customerId", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_PassesPresentNonStringValuesWithoutTruthiness()
    {
        var timestamp = new DateTime(2026, 8, 24, 10, 30, 0, DateTimeKind.Utc);
        var opaqueValue = new object();

        Assert.True(_handler.Validate(Row(8, ("Value", 0L)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", 0m)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", 12.5m)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", false)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", timestamp)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", opaqueValue)), Rule("Value")).IsValid);
    }

    [Fact]
    public void Validate_UsesConfiguredCustomErrorMessage()
    {
        var result = _handler.Validate(
            Row(9, ("CustomerId", null)),
            Rule("CustomerId", "A customer identity must be supplied."));

        Assert.Equal(
            "A customer identity must be supplied.",
            Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_UsesDefaultMessageWhenConfiguredMessageIsWhitespace()
    {
        var result = _handler.Validate(
            Row(9, ("CustomerId", null)),
            Rule("CustomerId", " \t "));

        Assert.Equal(
            "Upsert key field 'CustomerId' is required.",
            Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_DoesNotRequireConfigurationCollection()
    {
        var rule = Rule("CustomerId");
        rule.Configuration = null!;

        var result = _handler.Validate(
            Row(10, ("CustomerId", "customer-42")),
            rule);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_DoesNotMutateRow()
    {
        var row = Row(
            27,
            ("CustomerId", "  customer-42  "),
            ("Other", 0L),
            ("Missing", null));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("CustomerId"));

        Assert.True(result.IsValid);
        Assert.Equal(27, row.SourceRowNumber);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Fact]
    public void Validate_DoesNotMutateInvalidRow()
    {
        var row = Row(
            28,
            ("CustomerId", " \t "),
            ("Other", false),
            ("Amount", 0m));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("CustomerId"));

        Assert.False(result.IsValid);
        Assert.Equal(28, row.SourceRowNumber);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Validate_ThrowsWhenRuleFieldIsInvalid(string? field)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Validate(
                Row(11, ("CustomerId", "customer-42")),
                Rule(field)));

        Assert.Contains("field", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => _handler.Validate(null!, Rule("CustomerId")));
        Assert.Throws<ArgumentNullException>(
            () => _handler.Validate(Row(11), null!));
    }

    [Fact]
    public void Type_IsUpsertKeyRequired()
    {
        Assert.Equal(ValidationType.UpsertKeyRequired, _handler.Type);
    }

    [Fact]
    public void Validate_AfterTrim_FailsTransformedEmptyValueWithoutFurtherMutation()
    {
        var row = Row(15, ("CustomerId", " \t "));
        new TrimTransformationHandler().Apply(row, new TransformationRule
        {
            Type = TransformationType.Trim,
            SourceField = "CustomerId"
        });

        var result = _handler.Validate(row, Rule("CustomerId"));

        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, row.Values["CustomerId"]);
        Assert.Equal(15, result.Row.SourceRowNumber);
    }

    private static ValidationRule Rule(
        string? field,
        string errorMessage = "") => new()
    {
        Type = ValidationType.UpsertKeyRequired,
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
