using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class ConvertToIntegerTransformationHandlerTests
{
    private readonly ConvertToIntegerTransformationHandler _handler = new();

    public static TheoryData<object, long> SupportedValues => new()
    {
        { "42", 42L },
        { "-42", -42L },
        { "42 -", -42L },
        { "0", 0L },
        { "1.00000000000000000000000000000", 1L },
        { (sbyte)-8, -8L },
        { (byte)8, 8L },
        { (short)-16, -16L },
        { (ushort)16, 16L },
        { -32, -32L },
        { 32U, 32L },
        { -64L, -64L },
        { long.MinValue, long.MinValue },
        { long.MaxValue, long.MaxValue },
        { "-9223372036854775808", long.MinValue },
        { "9223372036854775807", long.MaxValue },
        { 64UL, 64L },
        { 42.0m, 42L },
        { 42.0d, 42L },
        { 42.0f, 42L }
    };

    public static TheoryData<object> FractionalValues => new()
    {
        "42.5",
        42.5m,
        42.5d,
        42.5f
    };

    public static TheoryData<object> UnsupportedValues => new()
    {
        true,
        new DateTime(2026, 8, 22),
        Guid.Parse("3f42d47e-1ca8-44cf-a91b-edebd9e52d08")
    };

    public static TheoryData<object> PrecisionLosingValues => new()
    {
        "0.00000000000000000000000000001",
        "-0.00000000000000000000000000001",
        "1.00000000000000000000000000001",
        "-1.00000000000000000000000000001",
        double.Epsilon,
        -double.Epsilon,
        float.Epsilon,
        -float.Epsilon
    };

    [Theory]
    [MemberData(nameof(SupportedValues))]
    public void Apply_ConvertsSupportedValuesToInt64(object input, long expected)
    {
        var row = Row(12, ("Value", input));

        var result = _handler.Apply(row, Rule("Value"), Culture("en-US"));

        Assert.Same(row, result);
        Assert.Equal(expected, Assert.IsType<long>(row.Values["Value"]));
    }

    [Theory]
    [InlineData("en-US", "  1,234.00  ", 1234L)]
    [InlineData("tr-TR", "  1.234,00  ", 1234L)]
    public void Apply_UsesConfiguredCultureForDecimalAndGroupingSeparators(
        string cultureName,
        string input,
        long expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = Culture(cultureName == "en-US" ? "tr-TR" : "en-US");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            var row = Row(4, ("Value", input));

            _handler.Apply(row, Rule("Value"), Culture(cultureName));

            Assert.Equal(expected, Assert.IsType<long>(row.Values["Value"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [MemberData(nameof(FractionalValues))]
    public void Apply_RejectsFractionalValuesWithoutChangingOriginal(object input)
    {
        var row = Row(5, ("Value", input));

        var exception = Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Contains("integral", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(input, row.Values["Value"]);
    }

    [Theory]
    [MemberData(nameof(PrecisionLosingValues))]
    public void Apply_RejectsOverPrecisionFractionalValuesWithoutChangingOriginal(object input)
    {
        var row = Row(5, ("Value", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Same(input, row.Values["Value"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("1E3")]
    public void Apply_RejectsMalformedTextWithoutChangingOriginal(string input)
    {
        var row = Row(6, ("Value", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Equal(input, row.Values["Value"]);
    }

    [Fact]
    public void Apply_DoesNotFallBackToAnotherCulture()
    {
        var row = Row(7, ("Value", "1,234.00"));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("Value"), Culture("tr-TR")));

        Assert.Equal("1,234.00", row.Values["Value"]);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    public void Apply_RejectsOutOfRangeText(string input)
    {
        var row = Row(8, ("Value", input));

        Assert.Throws<OverflowException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Equal(input, row.Values["Value"]);
    }

    [Fact]
    public void Apply_RejectsOutOfRangeAndNonFiniteNativeValues()
    {
        foreach (var input in new object[] { ulong.MaxValue, double.MaxValue, double.NaN, float.PositiveInfinity })
        {
            var row = Row(9, ("Value", input));

            Assert.Throws<OverflowException>(
                () => _handler.Apply(row, Rule("Value"), Culture("en-US")));
            Assert.Same(input, row.Values["Value"]);
        }
    }

    [Fact]
    public void Apply_PreservesNullAndUnrelatedFieldsAndUsesOrdinalLookup()
    {
        var nullRow = Row(10, ("Value", null));
        var row = Row(11, ("Value", "7"), ("value", "8"), ("Other", 9m));

        Assert.Same(nullRow, _handler.Apply(nullRow, Rule("Value"), Culture("en-US")));
        _handler.Apply(row, Rule("value"), Culture("en-US"));

        Assert.Null(nullRow.Values["Value"]);
        Assert.Equal("7", row.Values["Value"]);
        Assert.Equal(8L, Assert.IsType<long>(row.Values["value"]));
        Assert.Equal(9m, row.Values["Other"]);
    }

    [Theory]
    [MemberData(nameof(UnsupportedValues))]
    public void Apply_RejectsUnsupportedValues(object input)
    {
        var row = Row(12, ("Value", input));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(row, Rule("Value"), Culture("en-US")));

        Assert.Contains(input.GetType().FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Same(input, row.Values["Value"]);
    }

    [Fact]
    public void Apply_RejectsInvalidRuleMissingFieldAndCulturelessExecution()
    {
        var row = Row(13, ("Value", "1"));

        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, Rule(null), Culture("en-US")));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, Rule("Missing"), Culture("en-US")));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, Rule("Value")));
        Assert.Equal("1", row.Values["Value"]);
    }

    [Fact]
    public void Apply_RejectsNullArgumentsAndReportsType()
    {
        Assert.Equal(TransformationType.ConvertToInteger, _handler.Type);
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(null!, Rule("Value"), Culture("en-US")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(1, ("Value", "1")), null!, Culture("en-US")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(1, ("Value", "1")), Rule("Value"), null!));
    }

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

    private static TransformationRule Rule(string? sourceField) => new()
    {
        Type = TransformationType.ConvertToInteger,
        SourceField = sourceField
    };

    private static DataRow Row(long rowNumber, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = rowNumber };
        foreach (var (field, value) in values) row.Values.Add(field, value);
        return row;
    }
}
