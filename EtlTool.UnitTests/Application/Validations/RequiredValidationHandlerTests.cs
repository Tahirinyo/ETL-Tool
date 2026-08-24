using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class RequiredValidationHandlerTests
{
    private readonly RequiredValidationHandler _handler = new();

    [Theory]
    [InlineData("Ada")]
    [InlineData("  Ada  ")]
    public void Validate_PassesWhenStringValueIsPresent(string value)
    {
        var row = Row(12, ("Name", value));

        var result = _handler.Validate(row, Rule("Name"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Same(row, result.Row);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Validate_FailsWhenStringValueIsMissingOrBlank(string? value)
    {
        var row = Row(5, ("Name", value));

        var result = _handler.Validate(row, Rule("Name"));

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("Name", error.Field);
        Assert.Equal("Field 'Name' is required.", error.Message);
        Assert.Equal(value, row.Values["Name"]);
    }

    [Fact]
    public void Validate_FailsWhenConfiguredFieldIsAbsent()
    {
        var result = _handler.Validate(Row(6, ("Name", "Ada")), Rule("Email"));

        Assert.False(result.IsValid);
        Assert.Equal("Email", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_UsesOrdinalFieldLookup()
    {
        var result = _handler.Validate(Row(7, ("Name", "Ada")), Rule("name"));

        Assert.False(result.IsValid);
        Assert.Equal("name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void Validate_PassesPresentNonStringValues()
    {
        var timestamp = new DateTime(2026, 8, 24, 10, 30, 0, DateTimeKind.Utc);

        Assert.True(_handler.Validate(Row(8, ("Value", 0L)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", 42L)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", 0m)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", 12.5m)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", timestamp)), Rule("Value")).IsValid);
        Assert.True(_handler.Validate(Row(8, ("Value", false)), Rule("Value")).IsValid);
    }

    [Fact]
    public void Validate_UsesConfiguredCustomErrorMessage()
    {
        var result = _handler.Validate(
            Row(9, ("Name", null)),
            Rule("Name", "A name must be supplied."));

        Assert.Equal("A name must be supplied.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void Validate_DoesNotMutateRow()
    {
        var row = Row(27, ("Name", "  Ada  "), ("Other", 42L), ("Missing", null));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(row, Rule("Name"));

        Assert.True(result.IsValid);
        Assert.Equal(27, row.SourceRowNumber);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Validate_ThrowsWhenRuleFieldIsInvalid(string? field)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Validate(Row(10, ("Name", "Ada")), Rule(field)));

        Assert.Contains("field", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(null!, Rule("Name")));
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(Row(11), null!));
    }

    [Fact]
    public void Type_IsRequired()
    {
        Assert.Equal(ValidationType.Required, _handler.Type);
    }

    [Fact]
    public void Validate_AfterTrim_FailsWhitespaceOnlyValueWithoutFurtherMutation()
    {
        var row = Row(15, ("Name", " \t "));
        new TrimTransformationHandler().Apply(row, new TransformationRule
        {
            Type = TransformationType.Trim,
            SourceField = "Name"
        });

        var result = _handler.Validate(row, Rule("Name"));

        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, row.Values["Name"]);
        Assert.Equal(15, result.Row.SourceRowNumber);
    }

    private static ValidationRule Rule(string? field, string errorMessage = "") => new()
    {
        Type = ValidationType.Required,
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
