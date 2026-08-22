using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class ToUpperTransformationHandlerTests
{
    private readonly ToUpperTransformationHandler _handler = new();

    [Theory]
    [InlineData("hello", "HELLO")]
    [InlineData("HELLO", "HELLO")]
    [InlineData("", "")]
    [InlineData(" \t\r\n ", " \t\r\n ")]
    [InlineData(" hello  world ", " HELLO  WORLD ")]
    public void Apply_UppercasesStringWithoutChangingWhitespace(string input, string expected)
    {
        var row = Row(12, ("Name", input), ("Other", 42L));

        var result = _handler.Apply(row, Rule("Name"));

        Assert.Same(row, result.Row);
        Assert.Equal(expected, row.Values["Name"]);
        Assert.Equal(42L, row.Values["Other"]);
    }

    [Fact]
    public void Apply_PreservesNullAndNonStringValues()
    {
        var timestamp = new DateTime(2026, 8, 21, 10, 30, 0, DateTimeKind.Utc);
        var row = Row(4, ("NullValue", null), ("Value", 42L), ("CreatedAt", timestamp));

        _handler.Apply(row, Rule("NullValue"));
        _handler.Apply(row, Rule("Value"));
        _handler.Apply(row, Rule("CreatedAt"));

        Assert.Null(row.Values["NullValue"]);
        Assert.Equal(42L, Assert.IsType<long>(row.Values["Value"]));
        Assert.Equal(timestamp, Assert.IsType<DateTime>(row.Values["CreatedAt"]));
    }

    [Fact]
    public void Apply_UsesOrdinalCaseSensitiveFieldLookupAndPreservesRowMetadata()
    {
        var row = Row(27, ("Name", "Ada"), ("name", "alias"), ("Other", 7));

        var result = _handler.Apply(row, Rule("name"));

        Assert.Equal(27, result.Row.SourceRowNumber);
        Assert.Equal("Ada", result.Row.Values["Name"]);
        Assert.Equal("ALIAS", result.Row.Values["name"]);
        Assert.Equal(7, result.Row.Values["Other"]);
    }

    [Fact]
    public void Apply_UsesInvariantCasingIndependentOfAmbientCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var row = Row(1, ("Value", "iIıİ"));

            _handler.Apply(row, Rule("Value"));

            Assert.Equal("IIıİ", row.Values["Value"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Apply_ReportsType()
    {
        Assert.Equal(TransformationType.ToUpper, _handler.Type);
    }

    [Fact]
    public void Apply_ThrowsWhenFieldIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(8, ("Name", "Ada")), Rule("Missing")));

        Assert.Contains("Missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Apply_ThrowsWhenSourceFieldIsInvalid(string? sourceField)
    {
        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(9, ("Name", "Ada")), Rule(sourceField)));
    }

    [Fact]
    public void Apply_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(null!, Rule("Name")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(10, ("Name", "Ada")), null!));
    }

    private static TransformationRule Rule(string? sourceField) => new()
    {
        Type = TransformationType.ToUpper,
        SourceField = sourceField
    };

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
