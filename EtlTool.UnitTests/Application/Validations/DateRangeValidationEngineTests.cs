using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class DateRangeValidationEngineTests
{
    private readonly ValidationEngine _engine = new(
        new ValidationHandlerRegistry([
            new RequiredValidationHandler(),
            new DateRangeValidationHandler()
        ]));

    [Fact]
    public void Validate_PassesSourceCultureAndDateFormatToDateRangeHandler()
    {
        var rule = Rule("OccurredAt");
        rule.Configuration["Minimum"] = "31.12.2026";
        rule.Configuration["Maximum"] = "31.12.2026";

        var result = _engine.Validate(
            Row(("OccurredAt", new DateTime(2026, 12, 31))),
            [rule],
            new SourceOptions { CultureName = "en-US", DateFormat = "dd.MM.yyyy" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ReturnsDateRangeFailureThroughNormalValidationResult()
    {
        var rule = Rule("OccurredAt");
        rule.Configuration["Maximum"] = "2026-12-31";

        var result = _engine.Validate(
            Row(("OccurredAt", new DateTime(2027, 1, 1))),
            [rule],
            new SourceOptions { DateFormat = "yyyy-MM-dd" });

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("OccurredAt", error.Field);
        Assert.Equal("Field 'OccurredAt' must be at most 2026-12-31T00:00:00.0000000.", error.Message);
    }

    [Fact]
    public void Validate_ComposesRequiredAndDateRangeWithoutSharingResponsibilities()
    {
        var range = Rule("OccurredAt");
        range.Configuration["Minimum"] = "2026-01-01";
        var rules = new[]
        {
            Rule(ValidationType.Required, "OccurredAt"),
            range
        };

        var missing = _engine.Validate(
            Row(("OccurredAt", " ")),
            rules,
            new SourceOptions { DateFormat = "yyyy-MM-dd" });
        var unsupported = _engine.Validate(
            Row(("OccurredAt", "2026-01-01")),
            rules,
            new SourceOptions { DateFormat = "yyyy-MM-dd" });
        var valid = _engine.Validate(
            Row(("OccurredAt", new DateTime(2026, 1, 1))),
            rules,
            new SourceOptions { DateFormat = "yyyy-MM-dd" });

        Assert.Equal("Field 'OccurredAt' is required.", Assert.Single(missing.Errors).Message);
        Assert.Equal(
            "Field 'OccurredAt' must be at least 2026-01-01T00:00:00.0000000.",
            Assert.Single(unsupported.Errors).Message);
        Assert.True(valid.IsValid);
    }

    [Fact]
    public void Validate_DispatchesDateFormatCultureAndPlainHandlersExactlyOnce()
    {
        var dateFormatAware = new TrackingDateFormatHandler(ValidationType.DateRange);
        var cultureAware = new TrackingCultureHandler(ValidationType.NumericRange);
        var plain = new TrackingHandler(ValidationType.Required);
        var engine = new ValidationEngine(new ValidationHandlerRegistry([
            dateFormatAware,
            cultureAware,
            plain
        ]));
        var options = new SourceOptions { CultureName = "tr-TR", DateFormat = "dd.MM.yyyy" };

        _ = engine.Validate(Row(("Value", 1)), [Rule(ValidationType.DateRange, "Value")], options);
        _ = engine.Validate(Row(("Value", 1)), [Rule(ValidationType.NumericRange, "Value")], options);
        _ = engine.Validate(Row(("Value", 1)), [Rule(ValidationType.Required, "Value")], options);

        Assert.Equal(1, dateFormatAware.DateFormatInvocationCount);
        Assert.Equal(0, dateFormatAware.CultureInvocationCount);
        Assert.Equal("tr-TR", dateFormatAware.SourceCultureName);
        Assert.Equal("dd.MM.yyyy", dateFormatAware.DateFormat);
        Assert.Equal(1, cultureAware.InvocationCount);
        Assert.Equal("tr-TR", cultureAware.SourceCultureName);
        Assert.Equal(1, plain.InvocationCount);
    }

    private static ValidationRule Rule(string field) => Rule(ValidationType.DateRange, field);

    private static ValidationRule Rule(ValidationType type, string field) => new()
    {
        Type = type,
        Field = field
    };

    private static DataRow Row(params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = 2 };
        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }

    private sealed class TrackingDateFormatHandler(ValidationType type) : ISourceDateFormatValidationHandler
    {
        public ValidationType Type { get; } = type;

        public int CultureInvocationCount { get; private set; }

        public int DateFormatInvocationCount { get; private set; }

        public string? SourceCultureName { get; private set; }

        public string? DateFormat { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule) =>
            throw new InvalidOperationException("The test handler requires source context.");

        public ValidationResult Validate(
            DataRow row,
            ValidationRule rule,
            System.Globalization.CultureInfo sourceCulture)
        {
            CultureInvocationCount++;
            return ValidationResult.Valid(row);
        }

        public ValidationResult Validate(
            DataRow row,
            ValidationRule rule,
            System.Globalization.CultureInfo sourceCulture,
            string? dateFormat)
        {
            DateFormatInvocationCount++;
            SourceCultureName = sourceCulture.Name;
            DateFormat = dateFormat;
            return ValidationResult.Valid(row);
        }
    }

    private sealed class TrackingCultureHandler(ValidationType type) : ISourceCultureValidationHandler
    {
        public ValidationType Type { get; } = type;

        public int InvocationCount { get; private set; }

        public string? SourceCultureName { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule) =>
            throw new InvalidOperationException("The test handler requires source culture.");

        public ValidationResult Validate(
            DataRow row,
            ValidationRule rule,
            System.Globalization.CultureInfo sourceCulture)
        {
            InvocationCount++;
            SourceCultureName = sourceCulture.Name;
            return ValidationResult.Valid(row);
        }
    }

    private sealed class TrackingHandler(ValidationType type) : IValidationHandler
    {
        public ValidationType Type { get; } = type;

        public int InvocationCount { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule)
        {
            InvocationCount++;
            return ValidationResult.Valid(row);
        }
    }
}
