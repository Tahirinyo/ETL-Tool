using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class ValidationEngineAggregationTests
{
    [Fact]
    public void Validate_ReturnsValidOriginalRowWhenAllRulesPass()
    {
        var required = new TrackingHandler(ValidationType.Required);
        var email = new TrackingHandler(ValidationType.EmailFormat);
        var textLength = new TrackingHandler(ValidationType.TextLengthRange);
        var engine = Engine(required, email, textLength);
        var row = Row(("Name", "Ada"), ("Email", "ada@example.com"));
        var originalValues = row.Values.ToArray();

        var result = engine.Validate(
            row,
            [
                Rule(ValidationType.Required, "Name"),
                Rule(ValidationType.EmailFormat, "Email"),
                Rule(ValidationType.TextLengthRange, "Name")
            ],
            Options());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Same(row, result.Row);
        Assert.Equal(originalValues, row.Values.ToArray());
        Assert.Equal(1, required.InvocationCount);
        Assert.Equal(1, email.InvocationCount);
        Assert.Equal(1, textLength.InvocationCount);
        Assert.All(required.Rows.Concat(email.Rows).Concat(textLength.Rows), handledRow =>
            Assert.Same(row, handledRow));
    }

    [Fact]
    public void Validate_ContinuesAfterOneFailureAndReturnsExactlyThatError()
    {
        var expectedError = new ValidationError("Name", "Name is invalid.");
        var failing = new TrackingHandler(ValidationType.Required, expectedError);
        var succeeding = new TrackingHandler(ValidationType.EmailFormat);
        var engine = Engine(failing, succeeding);

        var result = engine.Validate(
            Row(("Name", null), ("Email", "ada@example.com")),
            [
                Rule(ValidationType.Required, "Name"),
                Rule(ValidationType.EmailFormat, "Email")
            ],
            Options());

        Assert.False(result.IsValid);
        Assert.Equal(expectedError, Assert.Single(result.Errors));
        Assert.Equal(1, failing.InvocationCount);
        Assert.Equal(1, succeeding.InvocationCount);
    }

    [Fact]
    public void Validate_AggregatesMultipleErrorsInConfiguredRuleOrder()
    {
        var textLengthError = new ValidationError("Name", "Text length custom message.");
        var requiredError = new ValidationError("CustomerId", "Required custom message.");
        var emailError = new ValidationError("Email", "Email custom message.");
        var textLength = new TrackingHandler(ValidationType.TextLengthRange, textLengthError);
        var required = new TrackingHandler(ValidationType.Required, requiredError);
        var email = new TrackingHandler(ValidationType.EmailFormat, emailError);
        var engine = Engine(textLength, required, email);

        var result = engine.Validate(
            Row(),
            [
                Rule(ValidationType.TextLengthRange, "Name"),
                Rule(ValidationType.Required, "CustomerId"),
                Rule(ValidationType.EmailFormat, "Email")
            ],
            Options());

        Assert.False(result.IsValid);
        Assert.Equal(
            [textLengthError, requiredError, emailError],
            result.Errors);
        Assert.Equal(1, textLength.InvocationCount);
        Assert.Equal(1, required.InvocationCount);
        Assert.Equal(1, email.InvocationCount);
    }

    [Fact]
    public void Validate_PreservesDistinctSameFieldErrors()
    {
        var engine = Engine(
            new RequiredValidationHandler(),
            new UpsertKeyValidationHandler());
        var row = Row(("CustomerId", " \t "));
        var originalValues = row.Values.ToArray();

        var result = engine.Validate(
            row,
            [
                Rule(ValidationType.Required, "CustomerId"),
                Rule(ValidationType.UpsertKeyRequired, "CustomerId")
            ],
            Options());

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(
            ["Field 'CustomerId' is required.", "Upsert key field 'CustomerId' is required."],
            result.Errors.Select(error => error.Message));
        Assert.All(result.Errors, error => Assert.Equal("CustomerId", error.Field));
        Assert.Same(row, result.Row);
        Assert.Equal(originalValues, row.Values.ToArray());
    }

    [Fact]
    public void Validate_AggregatesAcrossPlainCultureAndDateFormatDispatchExactlyOnce()
    {
        var plainError = new ValidationError("Name", "Name error.");
        var numericError = new ValidationError("Amount", "Amount error.");
        var dateError = new ValidationError("OccurredAt", "Date error.");
        var plain = new TrackingHandler(ValidationType.Required, plainError);
        var cultureAware = new TrackingCultureHandler(numericError);
        var dateFormatAware = new TrackingDateFormatHandler(dateError);
        var engine = Engine(plain, cultureAware, dateFormatAware);

        var result = engine.Validate(
            Row(),
            [
                Rule(ValidationType.Required, "Name"),
                Rule(ValidationType.NumericRange, "Amount"),
                Rule(ValidationType.DateRange, "OccurredAt")
            ],
            Options("tr-TR", "dd.MM.yyyy"));

        Assert.Equal([plainError, numericError, dateError], result.Errors);
        Assert.Equal(1, plain.InvocationCount);
        Assert.Equal(1, cultureAware.InvocationCount);
        Assert.Equal("tr-TR", cultureAware.SourceCultureName);
        Assert.Equal(1, dateFormatAware.DateFormatInvocationCount);
        Assert.Equal(0, dateFormatAware.CultureInvocationCount);
        Assert.Equal(0, dateFormatAware.PlainInvocationCount);
        Assert.Equal("tr-TR", dateFormatAware.SourceCultureName);
        Assert.Equal("dd.MM.yyyy", dateFormatAware.DateFormat);
    }

    [Fact]
    public void Validate_PropagatesLaterConfigurationExceptionAfterRowError()
    {
        var earlierFailure = new TrackingHandler(
            ValidationType.Required,
            new ValidationError("Name", "Name is required."));
        var engine = Engine(earlierFailure, new TextLengthValidationHandler());
        var invalidRange = Rule(ValidationType.TextLengthRange, "Name");
        invalidRange.Configuration["Minimum"] = "5";
        invalidRange.Configuration["Maximum"] = "3";

        var exception = Assert.Throws<InvalidOperationException>(() => engine.Validate(
            Row(("Name", " ")),
            [Rule(ValidationType.Required, "Name"), invalidRange],
            Options()));

        Assert.Contains("greater", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, earlierFailure.InvocationCount);
    }

    [Fact]
    public void Validate_StopsAtConfigurationExceptionBeforeLaterRule()
    {
        var laterHandler = new TrackingHandler(ValidationType.Required);
        var engine = Engine(new TextLengthValidationHandler(), laterHandler);
        var invalidRange = Rule(ValidationType.TextLengthRange, "Name");
        invalidRange.Configuration["Minimum"] = "5";
        invalidRange.Configuration["Maximum"] = "3";

        Assert.Throws<InvalidOperationException>(() => engine.Validate(
            Row(("Name", "Ada")),
            [invalidRange, Rule(ValidationType.Required, "Name")],
            Options()));

        Assert.Equal(0, laterHandler.InvocationCount);
    }

    [Fact]
    public void Validate_PropagatesMissingRegistrationAfterRowError()
    {
        var required = new TrackingHandler(
            ValidationType.Required,
            new ValidationError("Name", "Name is required."));
        var engine = Engine(required);

        Assert.Throws<KeyNotFoundException>(() => engine.Validate(
            Row(),
            [
                Rule(ValidationType.Required, "Name"),
                Rule(ValidationType.EmailFormat, "Email")
            ],
            Options()));

        Assert.Equal(1, required.InvocationCount);
    }

    [Fact]
    public void InvalidAggregate_DefensivelyCopiesErrorsAndRejectsInvalidCollections()
    {
        var row = Row();
        var errors = new List<ValidationError>
        {
            new("Name", "Name error."),
            new("Email", "Email error.")
        };

        var result = ValidationResult.Invalid(row, errors);
        errors.Clear();

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Same(row, result.Row);
        Assert.Throws<ArgumentException>(() =>
            ValidationResult.Invalid(row, Array.Empty<ValidationError>()));
        Assert.Throws<ArgumentException>(() =>
            ValidationResult.Invalid(row, new ValidationError[] { null! }));
        Assert.Throws<ArgumentNullException>(() =>
            ValidationResult.Invalid(row, (IReadOnlyList<ValidationError>)null!));
    }

    private static ValidationEngine Engine(params IValidationHandler[] handlers) =>
        new(new ValidationHandlerRegistry(handlers));

    private static ValidationRule Rule(ValidationType type, string field) => new()
    {
        Type = type,
        Field = field
    };

    private static DataRow Row(params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = 7 };
        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }

    private static SourceOptions Options(
        string cultureName = "",
        string? dateFormat = null) => new()
    {
        CultureName = cultureName,
        DateFormat = dateFormat
    };

    private sealed class TrackingHandler(
        ValidationType type,
        ValidationError? error = null) : IValidationHandler
    {
        public ValidationType Type { get; } = type;

        public int InvocationCount { get; private set; }

        public List<DataRow> Rows { get; } = [];

        public ValidationResult Validate(DataRow row, ValidationRule rule)
        {
            InvocationCount++;
            Rows.Add(row);
            return error is null
                ? ValidationResult.Valid(row)
                : ValidationResult.Invalid(row, error);
        }
    }

    private sealed class TrackingCultureHandler(ValidationError error)
        : ISourceCultureValidationHandler
    {
        public ValidationType Type => ValidationType.NumericRange;

        public int InvocationCount { get; private set; }

        public string? SourceCultureName { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule) =>
            throw new InvalidOperationException("The test handler requires source culture.");

        public ValidationResult Validate(
            DataRow row,
            ValidationRule rule,
            CultureInfo sourceCulture)
        {
            InvocationCount++;
            SourceCultureName = sourceCulture.Name;
            return ValidationResult.Invalid(row, error);
        }
    }

    private sealed class TrackingDateFormatHandler(ValidationError error)
        : ISourceDateFormatValidationHandler
    {
        public ValidationType Type => ValidationType.DateRange;

        public int PlainInvocationCount { get; private set; }

        public int CultureInvocationCount { get; private set; }

        public int DateFormatInvocationCount { get; private set; }

        public string? SourceCultureName { get; private set; }

        public string? DateFormat { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule)
        {
            PlainInvocationCount++;
            return ValidationResult.Invalid(row, error);
        }

        public ValidationResult Validate(
            DataRow row,
            ValidationRule rule,
            CultureInfo sourceCulture)
        {
            CultureInvocationCount++;
            return ValidationResult.Invalid(row, error);
        }

        public ValidationResult Validate(
            DataRow row,
            ValidationRule rule,
            CultureInfo sourceCulture,
            string? dateFormat)
        {
            DateFormatInvocationCount++;
            SourceCultureName = sourceCulture.Name;
            DateFormat = dateFormat;
            return ValidationResult.Invalid(row, error);
        }
    }
}
