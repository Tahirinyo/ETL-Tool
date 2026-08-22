using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class ConvertToStringTransformationHandlerTests
{
    private readonly ConvertToStringTransformationHandler _handler = new();

    [Theory]
    [InlineData("Ada")]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Apply_PreservesExistingStrings(string input)
    {
        var row = Row(12, ("Value", input));

        var result = _handler.Apply(row, Rule("Value"));

        Assert.Same(row, result);
        Assert.Equal(input, Assert.IsType<string>(row.Values["Value"]));
    }

    [Fact]
    public void Apply_PreservesNullValue()
    {
        var row = Row(3, ("Value", null));

        _handler.Apply(row, Rule("Value"));

        Assert.True(row.Values.ContainsKey("Value"));
        Assert.Null(row.Values["Value"]);
    }

    [Fact]
    public void Apply_FormatsSupportedScalarValuesWithInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var timestamp = new DateTime(2026, 8, 21, 10, 30, 0, DateTimeKind.Utc);
        var offsetTimestamp = new DateTimeOffset(2026, 8, 21, 10, 30, 0, TimeSpan.FromHours(3));

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var row = Row(
                4,
                ("Integer", 42L),
                ("Decimal", 1234.5m),
                ("Date", timestamp),
                ("OffsetDate", offsetTimestamp),
                ("Enabled", true));

            foreach (var field in row.Values.Keys.ToArray())
            {
                _handler.Apply(row, Rule(field));
            }

            Assert.Equal("42", Assert.IsType<string>(row.Values["Integer"]));
            Assert.Equal("1234.5", Assert.IsType<string>(row.Values["Decimal"]));
            Assert.Equal(timestamp.ToString(null, CultureInfo.InvariantCulture), Assert.IsType<string>(row.Values["Date"]));
            Assert.Equal(offsetTimestamp.ToString(null, CultureInfo.InvariantCulture), Assert.IsType<string>(row.Values["OffsetDate"]));
            Assert.Equal("True", Assert.IsType<string>(row.Values["Enabled"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Apply_UsesOrdinalFieldLookupAndPreservesUnrelatedFieldsAndMetadata()
    {
        var row = Row(27, ("Value", 7), ("value", 8), ("Other", new DateTime(2026, 8, 21)));

        var result = _handler.Apply(row, Rule("value"));

        Assert.Equal(27, result.SourceRowNumber);
        Assert.Equal(7, Assert.IsType<int>(result.Values["Value"]));
        Assert.Equal("8", Assert.IsType<string>(result.Values["value"]));
        Assert.Equal(new DateTime(2026, 8, 21), Assert.IsType<DateTime>(result.Values["Other"]));
    }

    [Fact]
    public void Apply_ReportsConvertToStringType()
    {
        Assert.Equal(TransformationType.ConvertToString, _handler.Type);
    }

    [Fact]
    public void Apply_ThrowsForUnsupportedValueType()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(8, ("Value", Guid.NewGuid())), Rule("Value")));

        Assert.Contains("Value", exception.Message, StringComparison.Ordinal);
        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ThrowsWhenFieldIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(8, ("Value", "Ada")), Rule("Missing")));

        Assert.Contains("Missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Apply_ThrowsWhenSourceFieldIsInvalid(string? sourceField)
    {
        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(9, ("Value", "Ada")), Rule(sourceField)));
    }

    [Fact]
    public void Apply_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(null!, Rule("Value")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(10, ("Value", "Ada")), null!));
    }

    private static TransformationRule Rule(string? sourceField) => new()
    {
        Type = TransformationType.ConvertToString,
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
