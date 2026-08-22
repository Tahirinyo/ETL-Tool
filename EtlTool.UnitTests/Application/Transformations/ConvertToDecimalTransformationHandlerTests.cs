using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class ConvertToDecimalTransformationHandlerTests
{
    private readonly ConvertToDecimalTransformationHandler _handler = new();

    public static TheoryData<object, decimal> SupportedValues => new()
    {
        { "42.5", 42.5m },
        { "-42.5", -42.5m },
        { "42.5 -", -42.5m },
        { "0", 0m },
        { "1.00000000000000000000000000000", 1m },
        { (sbyte)-8, -8m },
        { (byte)8, 8m },
        { (short)-16, -16m },
        { (ushort)16, 16m },
        { -32, -32m },
        { 32U, 32m },
        { -64L, -64m },
        { 64UL, 64m },
        { decimal.MinValue, decimal.MinValue },
        { decimal.MaxValue, decimal.MaxValue },
        { "-79228162514264337593543950335", decimal.MinValue },
        { "79228162514264337593543950335", decimal.MaxValue },
        { 42.5m, 42.5m },
        { 42.5d, 42.5m },
        { 42.5f, 42.5m },
        { 0.1d, 0.1m },
        { 0.1f, 0.1m }
    };

    public static TheoryData<object> UnsupportedValues => new()
    {
        false,
        new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.FromHours(3)),
        Guid.Parse("ae7505cf-a9cb-4460-af81-5311824d3599")
    };

    public static TheoryData<object> PrecisionLosingValues => new()
    {
        "0.00000000000000000000000000001",
        "-0.00000000000000000000000000001",
        "1.00000000000000000000000000001",
        "-1.00000000000000000000000000001",
        double.Epsilon,
        -double.Epsilon,
        1.2345678901234567d,
        float.Epsilon,
        -float.Epsilon,
        1.2345678f
    };

    [Theory]
    [MemberData(nameof(SupportedValues))]
    public void Apply_ConvertsSupportedValuesToDecimal(object input, decimal expected)
    {
        var row = Row(12, ("Value", input));

        var result = _handler.Apply(row, Rule("Value"), Culture("en-US"));

        Assert.Same(row, result.Row);
        Assert.Equal(expected, Assert.IsType<decimal>(row.Values["Value"]));
    }

    [Theory]
    [InlineData("en-US", "  1,234.56  ", "1234.56")]
    [InlineData("tr-TR", "  1.234,56  ", "1234.56")]
    public void Apply_UsesConfiguredCultureForDecimalAndGroupingSeparators(
        string cultureName,
        string input,
        string expectedText)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = Culture(cultureName == "en-US" ? "tr-TR" : "en-US");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            var row = Row(4, ("Value", input));

            _handler.Apply(row, Rule("Value"), Culture(cultureName));

            Assert.Equal(
                decimal.Parse(expectedText, CultureInfo.InvariantCulture),
                Assert.IsType<decimal>(row.Values["Value"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("1E3")]
    public void Apply_RejectsMalformedTextWithoutChangingOriginal(string input)
    {
        var row = Row(5, ("Value", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Equal(input, row.Values["Value"]);
    }

    [Theory]
    [MemberData(nameof(PrecisionLosingValues))]
    public void Apply_RejectsValuesThatCannotBeRepresentedWithoutPrecisionLoss(object input)
    {
        var row = Row(5, ("Value", input));

        Assert.Throws<OverflowException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Same(input, row.Values["Value"]);
    }

    [Fact]
    public void Apply_DoesNotFallBackToAnotherCulture()
    {
        var row = Row(6, ("Value", "1,234.56"));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("Value"), Culture("tr-TR")));

        Assert.Equal("1,234.56", row.Values["Value"]);
    }

    [Fact]
    public void Apply_RejectsOutOfRangeTextAndNativeValues()
    {
        foreach (var input in new object[]
        {
            "79228162514264337593543950336",
            double.MaxValue,
            double.NaN,
            float.NegativeInfinity
        })
        {
            var row = Row(7, ("Value", input));

            Assert.Throws<OverflowException>(
                () => _handler.Apply(row, Rule("Value"), Culture("en-US")));
            Assert.Same(input, row.Values["Value"]);
        }
    }

    [Fact]
    public void Apply_PreservesNullAndUnrelatedFieldsAndUsesOrdinalLookup()
    {
        var nullRow = Row(8, ("Value", null));
        var row = Row(9, ("Value", "7.5"), ("value", "8.5"), ("Other", 9L));

        Assert.Same(nullRow, _handler.Apply(nullRow, Rule("Value"), Culture("en-US")).Row);
        _handler.Apply(row, Rule("value"), Culture("en-US"));

        Assert.Null(nullRow.Values["Value"]);
        Assert.Equal("7.5", row.Values["Value"]);
        Assert.Equal(8.5m, Assert.IsType<decimal>(row.Values["value"]));
        Assert.Equal(9L, row.Values["Other"]);
    }

    [Theory]
    [MemberData(nameof(UnsupportedValues))]
    public void Apply_RejectsUnsupportedValues(object input)
    {
        var row = Row(10, ("Value", input));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Contains(input.GetType().FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Same(input, row.Values["Value"]);
    }

    [Fact]
    public void Apply_RejectsInvalidRuleMissingFieldAndCulturelessExecution()
    {
        var row = Row(11, ("Value", "1.5"));

        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, Rule(" "), Culture("en-US")));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, Rule("Missing"), Culture("en-US")));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, Rule("Value")));
        Assert.Equal("1.5", row.Values["Value"]);
    }

    [Fact]
    public void Apply_RejectsNullArgumentsAndReportsType()
    {
        Assert.Equal(TransformationType.ConvertToDecimal, _handler.Type);
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(null!, Rule("Value"), Culture("en-US")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(1, ("Value", "1")), null!, Culture("en-US")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(1, ("Value", "1")), Rule("Value"), null!));
    }

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

    private static TransformationRule Rule(string? sourceField) => new()
    {
        Type = TransformationType.ConvertToDecimal,
        SourceField = sourceField
    };

    private static DataRow Row(long rowNumber, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = rowNumber };
        foreach (var (field, value) in values) row.Values.Add(field, value);
        return row;
    }
}
