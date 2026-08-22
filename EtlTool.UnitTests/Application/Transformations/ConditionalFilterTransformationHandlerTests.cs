using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class ConditionalFilterTransformationHandlerTests
{
    private readonly ConditionalFilterTransformationHandler _handler = new();

    [Theory]
    [InlineData("Ada", FilterOperator.Equals, "Ada", true)]
    [InlineData("Ada", FilterOperator.NotEquals, "Grace", true)]
    [InlineData("Ada", FilterOperator.Equals, "ada", false)]
    [InlineData("Z", FilterOperator.GreaterThan, "a", false)]
    [InlineData("a", FilterOperator.LessThan, "b", true)]
    [InlineData("é", FilterOperator.GreaterThan, "e", true)]
    [InlineData("", FilterOperator.Equals, "", true)]
    [InlineData(" ", FilterOperator.Equals, "", false)]
    [InlineData("a", FilterOperator.LessThanOrEqual, "a", true)]
    [InlineData("b", FilterOperator.GreaterThanOrEqual, "a", true)]
    public void Apply_UsesOrdinalStringComparisonAndReturnsExplicitStatus(
        string value,
        FilterOperator filterOperator,
        string comparisonValue,
        bool expectedFiltered)
    {
        var row = Row(4, ("Name", value), ("Unchanged", 7L));
        var before = row.Values.ToArray();

        var result = _handler.Apply(
            row,
            Rule("Name", filterOperator, comparisonValue),
            Culture("en-US"),
            dateFormat: null);

        Assert.Same(row, result.Row);
        Assert.Equal(expectedFiltered, result.IsFiltered);
        Assert.Equal(
            expectedFiltered ? TransformationResultStatus.Filtered : TransformationResultStatus.Transformed,
            result.Status);
        Assert.Equal(before, row.Values);
    }

    [Fact]
    public void Apply_ComparesLongNumericallyRatherThanLexically()
    {
        var row = Row(5, ("Amount", 10L));

        var greater = _handler.Apply(
            row,
            Rule("Amount", FilterOperator.GreaterThan, "2"),
            Culture("en-US"),
            dateFormat: null);
        var less = _handler.Apply(
            row,
            Rule("Amount", FilterOperator.LessThan, "2"),
            Culture("en-US"),
            dateFormat: null);

        Assert.True(greater.IsFiltered);
        Assert.False(less.IsFiltered);
        Assert.Equal(10L, row.Values["Amount"]);
    }

    [Theory]
    [InlineData(-1L, FilterOperator.LessThan, "0", true)]
    [InlineData(0L, FilterOperator.Equals, "0", true)]
    [InlineData(0L, FilterOperator.GreaterThan, "0", false)]
    public void Apply_HandlesNegativeAndZeroIntegerComparisons(
        long value,
        FilterOperator filterOperator,
        string comparisonValue,
        bool expectedFiltered)
    {
        var result = _handler.Apply(
            Row(6, ("Amount", value)),
            Rule("Amount", filterOperator, comparisonValue),
            Culture("en-US"),
            dateFormat: null);

        Assert.Equal(expectedFiltered, result.IsFiltered);
    }

    [Theory]
    [InlineData(long.MinValue, FilterOperator.LessThanOrEqual, "-9223372036854775808", true)]
    [InlineData(long.MaxValue, FilterOperator.GreaterThanOrEqual, "9223372036854775807", true)]
    [InlineData(4L, FilterOperator.NotEquals, "5", true)]
    public void Apply_HandlesIntegerBoundsAndRemainingComparisonOperators(
        long value,
        FilterOperator filterOperator,
        string comparisonValue,
        bool expectedFiltered)
    {
        var result = _handler.Apply(
            Row(6, ("Amount", value)),
            Rule("Amount", filterOperator, comparisonValue),
            Culture("en-US"),
            dateFormat: null);

        Assert.Equal(expectedFiltered, result.IsFiltered);
    }

    [Fact]
    public void Apply_ParsesDecimalComparisonWithExplicitSourceCulture()
    {
        var row = Row(6, ("Amount", -1.5m));

        var result = _handler.Apply(
            row,
            Rule("Amount", FilterOperator.LessThanOrEqual, "-1,5"),
            Culture("tr-TR"),
            dateFormat: null);

        Assert.True(result.IsFiltered);
        Assert.Equal(-1.5m, row.Values["Amount"]);
    }

    [Theory]
    [InlineData("1.25", FilterOperator.Equals, "1.25", true)]
    [InlineData("1.25", FilterOperator.NotEquals, "2.25", true)]
    [InlineData("1.25", FilterOperator.GreaterThan, "1.24", true)]
    [InlineData("0", FilterOperator.LessThanOrEqual, "0", true)]
    [InlineData("79228162514264337593543950335", FilterOperator.Equals, "79228162514264337593543950335", true)]
    public void Apply_PreservesExactDecimalComparisonSemantics(
        string valueText,
        FilterOperator filterOperator,
        string comparisonValue,
        bool expectedFiltered)
    {
        var result = _handler.Apply(
            Row(6, ("Amount", decimal.Parse(valueText, CultureInfo.InvariantCulture))),
            Rule("Amount", filterOperator, comparisonValue),
            Culture("en-US"),
            dateFormat: null);

        Assert.Equal(expectedFiltered, result.IsFiltered);
    }

    [Theory]
    [InlineData(FilterOperator.Equals, "31.12.2026", true)]
    [InlineData(FilterOperator.GreaterThan, "30.12.2026", true)]
    [InlineData(FilterOperator.LessThan, "01.01.2027", true)]
    [InlineData(FilterOperator.GreaterThan, "01.01.2027", false)]
    public void Apply_ParsesDateComparisonWithExplicitCultureAndDateFormat(
        FilterOperator filterOperator,
        string comparisonValue,
        bool expectedFiltered)
    {
        var row = Row(7, ("OccurredAt", new DateTime(2026, 12, 31)));

        var result = _handler.Apply(
            row,
            Rule("OccurredAt", filterOperator, comparisonValue),
            Culture("tr-TR"),
            "dd.MM.yyyy");

        Assert.Equal(expectedFiltered, result.IsFiltered);
        Assert.Equal(new DateTime(2026, 12, 31), row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_UsesSourceCultureInsteadOfMachineCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = Culture("tr-TR");
            CultureInfo.CurrentUICulture = Culture("tr-TR");
            var row = Row(8, ("Amount", 1.5m));

            var result = _handler.Apply(
                row,
                Rule("Amount", FilterOperator.Equals, "1.5"),
                Culture("en-US"),
                dateFormat: null);

            Assert.True(result.IsFiltered);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Apply_UsesSourceCultureForDateValuesInsteadOfMachineCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = Culture("tr-TR");
            CultureInfo.CurrentUICulture = Culture("tr-TR");
            var result = _handler.Apply(
                Row(8, ("OccurredAt", new DateTime(2026, 12, 31))),
                Rule("OccurredAt", FilterOperator.Equals, "12/31/2026"),
                Culture("en-US"),
                dateFormat: null);

            Assert.True(result.IsFiltered);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Apply_NullValueNeverMatches()
    {
        var row = Row(9, ("Name", null));

        foreach (var filterOperator in Enum.GetValues<FilterOperator>().Where(value => value != FilterOperator.Unspecified))
        {
            var result = _handler.Apply(
                row,
                Rule("Name", filterOperator, ""),
                Culture("en-US"),
                dateFormat: null);

            Assert.False(result.IsFiltered);
            Assert.Null(row.Values["Name"]);
        }

        var textNull = _handler.Apply(
            row,
            Rule("Name", FilterOperator.Equals, "null"),
            Culture("en-US"),
            dateFormat: null);
        Assert.False(textNull.IsFiltered);
    }

    [Fact]
    public void Apply_RejectsMissingFieldAndUnsupportedRuntimeType()
    {
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(
            Row(10, ("Name", "Ada")),
            Rule("Missing", FilterOperator.Equals, "Ada"),
            Culture("en-US"),
            dateFormat: null));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(
            Row(11, ("Amount", 10)),
            Rule("Amount", FilterOperator.Equals, "10"),
            Culture("en-US"),
            dateFormat: null));
    }

    [Fact]
    public void Apply_RejectsMalformedOrIncompatibleConfigurationWithoutMutatingRow()
    {
        var row = Row(12, ("Amount", 10L));
        var malformedNumber = Rule("Amount", FilterOperator.Equals, "ten");
        var numericOperator = Rule("Amount", FilterOperator.Equals, "10");
        numericOperator.Configuration["Operator"] = "1";
        var missingValue = Rule("Amount", FilterOperator.Equals, "10");
        missingValue.Configuration.Remove("Value");
        var missingOperator = Rule("Amount", FilterOperator.Equals, "10");
        missingOperator.Configuration.Remove("Operator");

        Assert.Throws<FormatException>(() => _handler.Apply(
            row, malformedNumber, Culture("en-US"), dateFormat: null));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(
            row, numericOperator, Culture("en-US"), dateFormat: null));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(
            row, missingValue, Culture("en-US"), dateFormat: null));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(
            row, missingOperator, Culture("en-US"), dateFormat: null));
        Assert.Equal(10L, row.Values["Amount"]);
    }

    [Theory]
    [InlineData("31.12", null)]
    [InlineData("31.02.2026", "dd.MM.yyyy")]
    [InlineData("2026-12-31T12:00:00Z", null)]
    public void Apply_RejectsInvalidDateComparisonWithoutMutatingRow(
        string comparisonValue,
        string? dateFormat)
    {
        var row = Row(13, ("OccurredAt", new DateTime(2026, 12, 31)));

        Assert.Throws<FormatException>(() => _handler.Apply(
            row,
            Rule("OccurredAt", FilterOperator.Equals, comparisonValue),
            Culture("tr-TR"),
            dateFormat));
        Assert.Equal(new DateTime(2026, 12, 31), row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_UsesSharedDateParserForTimeAndFractionalSeconds()
    {
        var timestamp = new DateTime(2026, 12, 31, 22, 15, 16).AddTicks(1_234_567);
        var row = Row(14, ("OccurredAt", timestamp));

        var equals = _handler.Apply(
            row,
            Rule("OccurredAt", FilterOperator.Equals, "12/31/2026 22:15:16.1234567"),
            Culture("en-US"),
            dateFormat: null);
        var beforeNextSecond = _handler.Apply(
            row,
            Rule("OccurredAt", FilterOperator.LessThan, "12/31/2026 22:15:17"),
            Culture("en-US"),
            dateFormat: null);

        Assert.True(equals.IsFiltered);
        Assert.True(beforeNextSecond.IsFiltered);
        Assert.Equal(timestamp, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_RejectsInvalidArgumentsAndCulturelessExecution()
    {
        var row = Row(13, ("Name", "Ada"));
        var rule = Rule("Name", FilterOperator.Equals, "Ada");

        Assert.Throws<ArgumentNullException>(() => _handler.Apply(null!, rule, Culture("en-US"), null));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(row, null!, Culture("en-US"), null));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(row, rule, null!, null));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, rule));
        Assert.Equal(TransformationType.FilterRow, _handler.Type);
    }

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

    private static TransformationRule Rule(
        string sourceField,
        FilterOperator filterOperator,
        string value) => new()
        {
            Id = Guid.NewGuid(),
            Type = TransformationType.FilterRow,
            Order = 1,
            SourceField = sourceField,
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Operator"] = filterOperator.ToString(),
                ["Value"] = value
            }
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
