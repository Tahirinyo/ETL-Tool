using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class ConvertToDateTransformationHandlerTests
{
    private readonly ConvertToDateTransformationHandler _handler = new();

    [Fact]
    public void Apply_PreservesNativeDateTimeIncludingTimeAndKind()
    {
        var timestamp = new DateTime(2026, 8, 22, 12, 34, 56, DateTimeKind.Utc);
        var row = Row(2, ("OccurredAt", timestamp));

        var result = _handler.Apply(row, Rule("OccurredAt"), Culture("tr-TR"), "dd.MM.yyyy");

        Assert.Same(row, result);
        Assert.Equal(timestamp, Assert.IsType<DateTime>(row.Values["OccurredAt"]));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)row.Values["OccurredAt"]!).Kind);
    }

    [Theory]
    [InlineData("31.12.2026", "tr-TR", 2026, 12, 31, 0, 0, 0)]
    [InlineData("12/31/2026 22:15:16", "en-US", 2026, 12, 31, 22, 15, 16)]
    [InlineData("29.02.2024", "tr-TR", 2024, 2, 29, 0, 0, 0)]
    public void Apply_ParsesValidTextWithConfiguredCulture(
        string input,
        string cultureName,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var row = Row(3, ("OccurredAt", input));

        _handler.Apply(row, Rule("OccurredAt"), Culture(cultureName), dateFormat: null);

        var actual = Assert.IsType<DateTime>(row.Values["OccurredAt"]);
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), actual);
        Assert.Equal(DateTimeKind.Unspecified, actual.Kind);
    }

    [Fact]
    public void Apply_UsesConfiguredCultureInsteadOfProcessCultureForAmbiguousDate()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = Culture("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            var turkishRow = Row(4, ("OccurredAt", "03/04/2026"));

            _handler.Apply(turkishRow, Rule("OccurredAt"), Culture("tr-TR"), dateFormat: null);

            Assert.Equal(
                new DateTime(2026, 4, 3),
                Assert.IsType<DateTime>(turkishRow.Values["OccurredAt"]));

            CultureInfo.CurrentCulture = Culture("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            var usRow = Row(5, ("OccurredAt", "03/04/2026"));

            _handler.Apply(usRow, Rule("OccurredAt"), Culture("en-US"), dateFormat: null);

            Assert.Equal(
                new DateTime(2026, 3, 4),
                Assert.IsType<DateTime>(usRow.Values["OccurredAt"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Apply_DoesNotFallBackToAnotherCulture()
    {
        const string input = "31 Aralık 2026";
        var row = Row(6, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat: null));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData(" 31.12.2026 ", "dd.MM.yyyy", 2026, 12, 31)]
    [InlineData("31/12/2026", "dd/MM/yyyy", 2026, 12, 31)]
    [InlineData("0001-01-01", "yyyy-MM-dd", 1, 1, 1)]
    [InlineData("9999-12-31", "yyyy-MM-dd", 9999, 12, 31)]
    public void Apply_UsesConfiguredExactFormat(
        string input,
        string dateFormat,
        int year,
        int month,
        int day)
    {
        var row = Row(7, ("OccurredAt", input));

        _handler.Apply(row, Rule("OccurredAt"), Culture("tr-TR"), dateFormat);

        Assert.Equal(
            new DateTime(year, month, day),
            Assert.IsType<DateTime>(row.Values["OccurredAt"]));
    }

    [Fact]
    public void Apply_UsesConfiguredCultureForExactFormatText()
    {
        var row = Row(8, ("OccurredAt", "31 Aral\u0131k 2026"));

        _handler.Apply(
            row,
            Rule("OccurredAt"),
            Culture("tr-TR"),
            "dd MMMM yyyy");

        Assert.Equal(
            new DateTime(2026, 12, 31),
            Assert.IsType<DateTime>(row.Values["OccurredAt"]));
    }

    [Fact]
    public void Apply_UsesConfiguredCultureForYearBearingStandardDateFormat()
    {
        var row = Row(8, ("OccurredAt", "31.12.2026"));

        _handler.Apply(row, Rule("OccurredAt"), Culture("tr-TR"), "d");

        Assert.Equal(
            new DateTime(2026, 12, 31),
            Assert.IsType<DateTime>(row.Values["OccurredAt"]));
    }

    [Fact]
    public void Apply_RejectsStandardFormatWithoutCalendarYear()
    {
        const string input = "December 31";
        var row = Row(8, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), "M"));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData("Sat, 22 Aug 2026 12:00:00 GMT", "R")]
    [InlineData("Sat, 22 Aug 2026 12:00:00 GMT", "r")]
    [InlineData("2026-08-22 12:00:00Z", "u")]
    public void Apply_RejectsTimezoneBearingStandardFormatsWithoutChangingOriginal(
        string input,
        string dateFormat)
    {
        var row = Row(8, ("OccurredAt", input));

        var exception = Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat));

        Assert.Contains("timezone or offset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData("2026-12-31", "dd.MM.yyyy")]
    [InlineData("31.12.2026 10:30", "dd.MM.yyyy")]
    public void Apply_ExactFormatDoesNotFallBackToGeneralParsing(
        string input,
        string dateFormat)
    {
        var row = Row(8, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("tr-TR"), dateFormat));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_RejectsTimeOnlyTextWhenExactFormatWouldSupplyNoDate()
    {
        const string input = "12:30";
        var row = Row(9, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("tr-TR"), "HH:mm"));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData("en-US", "12/31")]
    [InlineData("tr-TR", "31/12")]
    [InlineData("en-US", "1/2")]
    [InlineData("tr-TR", "1/2")]
    public void Apply_RejectsPartialDateTextThatWouldDefaultTheCurrentYear(
        string cultureName,
        string input)
    {
        var row = Row(10, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture(cultureName), dateFormat: null));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData("en-US", "12/31/2026")]
    [InlineData("tr-TR", "31/12/2026")]
    public void Apply_AcceptsCompleteCultureAwareDates(
        string cultureName,
        string input)
    {
        var row = Row(11, ("OccurredAt", input));

        _handler.Apply(row, Rule("OccurredAt"), Culture(cultureName), dateFormat: null);

        Assert.Equal(
            new DateTime(2026, 12, 31),
            Assert.IsType<DateTime>(row.Values["OccurredAt"]));
    }

    [Theory]
    [InlineData("12/31/2026 22:15:16.1", 1_000_000)]
    [InlineData("12/31/2026 22:15:16.123", 1_230_000)]
    [InlineData("12/31/2026 22:15:16.1234567", 1_234_567)]
    public void Apply_ParsesGeneralDateTimeWithVariableFractionalSeconds(
        string input,
        long expectedFractionalTicks)
    {
        var row = Row(11, ("OccurredAt", input));

        _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat: null);

        var actual = Assert.IsType<DateTime>(row.Values["OccurredAt"]);
        Assert.Equal(new DateTime(2026, 12, 31, 22, 15, 16), actual.AddTicks(-expectedFractionalTicks));
        Assert.Equal(expectedFractionalTicks, actual.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Apply_RejectsFractionalTimeWithIncompleteDateWithoutChangingOriginal()
    {
        const string input = "12/31 22:15:16.123";
        var row = Row(11, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat: null));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_RejectsGeneralDateTimeWithMoreThanSevenFractionalDigitsWithoutChangingOriginal()
    {
        const string input = "12/31/2026 22:15:16.12345678";
        var row = Row(11, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat: null));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_RejectsExactFormatThatOmitsCalendarYear()
    {
        const string input = "12/31";
        var row = Row(12, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), "MM/dd"));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_RejectsExactFormatWithCalendarYearOnlyInQuotedLiteral()
    {
        var currentCalendarYear = Culture("en-US").DateTimeFormat.Calendar.GetYear(DateTime.Today);
        var input = $"12/31 {currentCalendarYear}";
        var row = Row(12, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(
                row,
                Rule("OccurredAt"),
                Culture("en-US"),
                $"MM/dd '{currentCalendarYear}'"));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData("12/31 yyyy", "MM/dd 'yyyy'")]
    [InlineData("12/31 yyyy", "MM/dd \"yyyy\"")]
    [InlineData("12/31 y", @"MM/dd \y")]
    public void Apply_RejectsExactFormatWithYearTokensOnlyInLiteralsOrEscapes(
        string input,
        string dateFormat)
    {
        var row = Row(12, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData("12/31/6", "MM/dd/y", 2006)]
    [InlineData("12/31/26", "MM/dd/yy", 2026)]
    [InlineData("12/31/026", "MM/dd/yyy", 26)]
    [InlineData("12/31/2026", "MM/dd/yyyy", 2026)]
    [InlineData("12/31/02026", "MM/dd/yyyyy", 2026)]
    public void Apply_AcceptsSupportedExactYearTokens(
        string input,
        string dateFormat,
        int expectedYear)
    {
        var row = Row(12, ("OccurredAt", input));

        _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat);

        Assert.Equal(
            expectedYear,
            Assert.IsType<DateTime>(row.Values["OccurredAt"]).Year);
    }

    [Fact]
    public void Apply_AcceptsTwoDigitYearUsingConfiguredNonGregorianCalendar()
    {
        var sourceCulture = Culture("ar-SA");
        var row = Row(12, ("OccurredAt", "01/01/48"));

        _handler.Apply(row, Rule("OccurredAt"), sourceCulture, "dd/MM/yy");

        var parsed = Assert.IsType<DateTime>(row.Values["OccurredAt"]);
        Assert.True(DateTime.TryParseExact(
            "01/01/48",
            "dd/MM/yy",
            sourceCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var expected));
        Assert.Equal(expected, parsed);
        Assert.Equal(1448, sourceCulture.DateTimeFormat.Calendar.GetYear(parsed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    [InlineData("30/02/2026")]
    [InlineData("31/04/2026")]
    [InlineData("29/02/2025")]
    [InlineData("10000-01-01")]
    [InlineData("12:30")]
    public void Apply_RejectsInvalidDatesWithoutChangingOriginal(string input)
    {
        var row = Row(9, ("OccurredAt", input));

        Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("tr-TR"), dateFormat: null));

        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData("2026-08-22T12:00:00Z")]
    [InlineData("2026-08-22T12:00:00+03:00")]
    public void Apply_RejectsOffsetBearingTextWithoutChangingOriginal(string input)
    {
        var row = Row(10, ("OccurredAt", input));

        var exception = Assert.Throws<FormatException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat: null));

        Assert.Contains("timezone or offset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(input, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_PreservesNullAndUnrelatedFieldsAndUsesOrdinalLookup()
    {
        var nullRow = Row(11, ("OccurredAt", null));
        var row = Row(
            12,
            ("OccurredAt", "12/31/2026"),
            ("occurredAt", "31/12/2026"),
            ("Other", 42L));

        Assert.Same(
            nullRow,
            _handler.Apply(nullRow, Rule("OccurredAt"), Culture("en-US"), dateFormat: null));
        _handler.Apply(row, Rule("occurredAt"), Culture("tr-TR"), dateFormat: null);

        Assert.Null(nullRow.Values["OccurredAt"]);
        Assert.Equal("12/31/2026", row.Values["OccurredAt"]);
        Assert.Equal(new DateTime(2026, 12, 31), row.Values["occurredAt"]);
        Assert.Equal(42L, row.Values["Other"]);
    }

    [Theory]
    [MemberData(nameof(UnsupportedValues))]
    public void Apply_RejectsUnsupportedValuesWithoutChangingOriginal(object input)
    {
        var row = Row(13, ("OccurredAt", input));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(row, Rule("OccurredAt"), Culture("en-US"), dateFormat: null));

        Assert.Contains(input.GetType().FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Same(input, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_RejectsInvalidRuleMissingFieldAndCulturelessExecution()
    {
        var row = Row(14, ("OccurredAt", "12/31/2026"));

        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(row, Rule(" "), Culture("en-US"), dateFormat: null));
        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(row, Rule("Missing"), Culture("en-US"), dateFormat: null));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(row, Rule("OccurredAt")));
        Assert.Equal("12/31/2026", row.Values["OccurredAt"]);
    }

    [Fact]
    public void Apply_RejectsNullArgumentsAndReportsType()
    {
        Assert.Equal(TransformationType.ConvertToDate, _handler.Type);
        Assert.Throws<ArgumentNullException>(
            () => _handler.Apply(null!, Rule("OccurredAt"), Culture("en-US"), dateFormat: null));
        Assert.Throws<ArgumentNullException>(
            () => _handler.Apply(Row(1, ("OccurredAt", "12/31/2026")), null!, Culture("en-US"), dateFormat: null));
        Assert.Throws<ArgumentNullException>(
            () => _handler.Apply(Row(1, ("OccurredAt", "12/31/2026")), Rule("OccurredAt"), null!, dateFormat: null));
    }

    public static TheoryData<object> UnsupportedValues => new()
    {
        true,
        20260822L,
        TimeSpan.FromHours(12),
        new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.FromHours(3))
    };

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

    private static TransformationRule Rule(string? sourceField) => new()
    {
        Type = TransformationType.ConvertToDate,
        SourceField = sourceField
    };

    private static DataRow Row(long rowNumber, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = rowNumber };
        foreach (var (field, value) in values) row.Values.Add(field, value);
        return row;
    }
}
