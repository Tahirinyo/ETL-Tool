using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class DateRangeValidationHandlerTests
{
    private readonly DateRangeValidationHandler _handler = new();

    [Theory]
    [InlineData(2025, 12, 31, false)]
    [InlineData(2026, 1, 1, true)]
    [InlineData(2026, 6, 15, true)]
    [InlineData(2026, 12, 31, true)]
    [InlineData(2027, 1, 1, false)]
    public void Validate_AppliesBothBoundsInclusively(int year, int month, int day, bool expectedValid)
    {
        var row = Row(4, ("OccurredAt", new DateTime(year, month, day)));

        var result = _handler.Validate(
            row,
            Rule("OccurredAt", minimum: "2026-01-01", maximum: "2026-12-31"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");

        Assert.Equal(expectedValid, result.IsValid);
        Assert.Same(row, result.Row);
        Assert.Equal(new DateTime(year, month, day), row.Values["OccurredAt"]);
    }

    [Fact]
    public void Validate_AppliesMinimumOnlyAndMaximumOnlyRules()
    {
        Assert.True(_handler.Validate(
            Row(5, ("OccurredAt", new DateTime(2026, 1, 1))),
            Rule("OccurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd").IsValid);
        Assert.False(_handler.Validate(
            Row(5, ("OccurredAt", new DateTime(2025, 12, 31))),
            Rule("OccurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd").IsValid);
        Assert.True(_handler.Validate(
            Row(5, ("OccurredAt", new DateTime(2026, 12, 31))),
            Rule("OccurredAt", maximum: "2026-12-31"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd").IsValid);
        Assert.False(_handler.Validate(
            Row(5, ("OccurredAt", new DateTime(2027, 1, 1))),
            Rule("OccurredAt", maximum: "2026-12-31"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd").IsValid);
    }

    [Fact]
    public void Validate_UsesFullDateTimePrecisionWithoutNormalizingValue()
    {
        var minimum = new DateTime(2026, 12, 31, 22, 15, 16).AddTicks(1_234_567);
        var row = Row(6, ("OccurredAt", minimum), ("Other", 42L));
        var originalValues = row.Values.ToArray();

        var atMinimum = _handler.Validate(
            row,
            Rule(
                "OccurredAt",
                minimum: "2026-12-31 22:15:16.1234567",
                maximum: "2026-12-31 22:15:17"),
            CultureInfo.GetCultureInfo("en-US"),
            "yyyy-MM-dd HH:mm:ss.FFFFFFF");
        var beforeMinimum = _handler.Validate(
            Row(7, ("OccurredAt", minimum.AddTicks(-1))),
            Rule("OccurredAt", minimum: "2026-12-31 22:15:16.1234567"),
            CultureInfo.GetCultureInfo("en-US"),
            "yyyy-MM-dd HH:mm:ss.FFFFFFF");

        Assert.True(atMinimum.IsValid);
        Assert.False(beforeMinimum.IsValid);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Fact]
    public void Validate_UsesFullTimeOfDayForSameDayMaximumBoundary()
    {
        var maximum = new DateTime(2026, 12, 31, 12, 0, 0).AddTicks(1_234_567);
        var rule = Rule("OccurredAt", maximum: "2026-12-31 12:00:00.1234567");

        var beforeMaximum = _handler.Validate(
            Row(7, ("OccurredAt", maximum.AddTicks(-1))),
            rule,
            CultureInfo.GetCultureInfo("en-US"),
            "yyyy-MM-dd HH:mm:ss.FFFFFFF");
        var atMaximum = _handler.Validate(
            Row(7, ("OccurredAt", maximum)),
            rule,
            CultureInfo.GetCultureInfo("en-US"),
            "yyyy-MM-dd HH:mm:ss.FFFFFFF");
        var afterMaximum = _handler.Validate(
            Row(7, ("OccurredAt", maximum.AddTicks(1))),
            rule,
            CultureInfo.GetCultureInfo("en-US"),
            "yyyy-MM-dd HH:mm:ss.FFFFFFF");

        Assert.True(beforeMaximum.IsValid);
        Assert.True(atMaximum.IsValid);
        Assert.False(afterMaximum.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Validate_PassesOptionalNullOrBlankValues(object? value)
    {
        var result = _handler.Validate(
            Row(8, ("OccurredAt", value)),
            Rule("OccurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PassesWhenConfiguredFieldIsAbsentAndUsesOrdinalFieldLookup()
    {
        var absent = _handler.Validate(
            Row(9, ("Other", new DateTime(2026, 1, 1))),
            Rule("OccurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");
        var ordinal = _handler.Validate(
            Row(9, ("OccurredAt", new DateTime(2025, 1, 1))),
            Rule("occurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");

        Assert.True(absent.IsValid);
        Assert.True(ordinal.IsValid);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-01-01")]
    [InlineData(20260824L)]
    [InlineData(true)]
    public void Validate_FailsPresentUnsupportedValuesWithoutCoercion(object value)
    {
        var row = Row(10, ("OccurredAt", value), ("Other", 42L));
        var originalValues = row.Values.ToArray();

        var result = _handler.Validate(
            row,
            Rule("OccurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");

        Assert.False(result.IsValid);
        Assert.Equal("OccurredAt", Assert.Single(result.Errors).Field);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Fact]
    public void Validate_FailsDateTimeOffsetWithoutCoercion()
    {
        var offsetDate = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var row = Row(10, ("OccurredAt", offsetDate));

        var result = _handler.Validate(
            row,
            Rule("OccurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");

        Assert.False(result.IsValid);
        Assert.Equal(offsetDate, row.Values["OccurredAt"]);
    }

    [Fact]
    public void Validate_UsesCustomAndDeterministicDefaultMessages()
    {
        var defaultResult = _handler.Validate(
            Row(11, ("OccurredAt", new DateTime(2025, 12, 31))),
            Rule("OccurredAt", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");
        var customResult = _handler.Validate(
            Row(11, ("OccurredAt", new DateTime(2025, 12, 31))),
            Rule("OccurredAt", "Outside permitted range.", minimum: "2026-01-01"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd");

        Assert.Equal(
            "Field 'OccurredAt' must be at least 2026-01-01T00:00:00.0000000.",
            Assert.Single(defaultResult.Errors).Message);
        Assert.Equal("Outside permitted range.", Assert.Single(customResult.Errors).Message);
    }

    [Fact]
    public void Validate_UsesConfiguredCultureAndExactDateFormatInsteadOfCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            var result = _handler.Validate(
                Row(12, ("OccurredAt", new DateTime(2026, 4, 3))),
                Rule("OccurredAt", minimum: "03/04/2026", maximum: "04/04/2026"),
                CultureInfo.GetCultureInfo("tr-TR"),
                "dd/MM/yyyy");

            Assert.True(result.IsValid);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Validate_AcceptsDateConversionOutput()
    {
        var row = Row(13, ("OccurredAt", "31.12.2026"));
        var conversion = new ConvertToDateTransformationHandler();

        conversion.Apply(
            row,
            new TransformationRule { Type = TransformationType.ConvertToDate, SourceField = "OccurredAt" },
            CultureInfo.GetCultureInfo("tr-TR"),
            "dd.MM.yyyy");
        var result = _handler.Validate(
            row,
            Rule("OccurredAt", minimum: "01.01.2026", maximum: "31.12.2026"),
            CultureInfo.GetCultureInfo("tr-TR"),
            "dd.MM.yyyy");

        Assert.True(result.IsValid);
        Assert.IsType<DateTime>(row.Values["OccurredAt"]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(null, " ")]
    [InlineData("not-a-date", null)]
    [InlineData(null, "2026-13-01")]
    [InlineData("2026-12-31", "2026-01-01")]
    public void Validate_RejectsInvalidRangeConfiguration(string? minimum, string? maximum)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(14, ("OccurredAt", new DateTime(2026, 6, 1))),
            Rule("OccurredAt", minimum: minimum, maximum: maximum),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd"));

        Assert.Contains("date range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsCaseMismatchedConfigurationKeyAndMalformedExactFormat()
    {
        var mismatchedKey = Rule("OccurredAt");
        mismatchedKey.Configuration["minimum"] = "2026-01-01";

        var keyException = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(15, ("OccurredAt", new DateTime(2026, 6, 1))),
            mismatchedKey,
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd"));
        var formatException = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(15, ("OccurredAt", new DateTime(2026, 6, 1))),
            Rule("OccurredAt", minimum: "01/01/2026"),
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd"));

        Assert.Contains("exact ordinal casing", keyException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<FormatException>(formatException.InnerException);
    }

    [Theory]
    [InlineData("Minimum", "minimum")]
    [InlineData("minimum", "Minimum")]
    [InlineData("Maximum", "maximum")]
    [InlineData("maximum", "Maximum")]
    public void Validate_RejectsMixedCaseDuplicateConfigurationKeysRegardlessOfInsertionOrder(
        string firstKey,
        string secondKey)
    {
        var rule = Rule("OccurredAt");
        rule.Configuration[firstKey] = "2026-01-01";
        rule.Configuration[secondKey] = "2030-01-01";

        var exception = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(15, ("OccurredAt", new DateTime(2026, 6, 1))),
            rule,
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd"));

        Assert.Contains("exact ordinal casing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("12/31", "en-US", null)]
    [InlineData("12/31", "en-US", "MM/dd")]
    [InlineData("2026-12-31T12:00:00Z", "en-US", null)]
    public void Validate_RejectsSharedParserUnsupportedBoundConfiguration(
        string bound,
        string cultureName,
        string? dateFormat)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            Row(16, ("OccurredAt", new DateTime(2026, 12, 31))),
            Rule("OccurredAt", minimum: bound),
            CultureInfo.GetCultureInfo(cultureName),
            dateFormat));

        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public void Validate_RejectsInvalidArgumentsAndCulturelessExecution()
    {
        var row = Row(16, ("OccurredAt", new DateTime(2026, 1, 1)));
        var rule = Rule("OccurredAt", minimum: "2026-01-01");

        Assert.Equal(ValidationType.DateRange, _handler.Type);
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(null!, rule, CultureInfo.InvariantCulture, "yyyy-MM-dd"));
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(row, null!, CultureInfo.InvariantCulture, "yyyy-MM-dd"));
        Assert.Throws<ArgumentNullException>(() => _handler.Validate(row, rule, null!, "yyyy-MM-dd"));
        Assert.Throws<InvalidOperationException>(() => _handler.Validate(row, rule));
    }

    [Fact]
    public void Validate_RejectsMissingFieldAndConfigurationCollection()
    {
        var row = Row(17, ("OccurredAt", new DateTime(2026, 1, 1)));
        var missingField = Rule(" ", minimum: "2026-01-01");
        var missingConfiguration = Rule("OccurredAt");
        missingConfiguration.Configuration = null!;

        var fieldException = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            row,
            missingField,
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd"));
        var configurationException = Assert.Throws<InvalidOperationException>(() => _handler.Validate(
            row,
            missingConfiguration,
            CultureInfo.InvariantCulture,
            "yyyy-MM-dd"));

        Assert.Contains("field", fieldException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("configuration", configurationException.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ValidationRule Rule(
        string field,
        string errorMessage = "",
        string? minimum = null,
        string? maximum = null)
    {
        var rule = new ValidationRule
        {
            Type = ValidationType.DateRange,
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
