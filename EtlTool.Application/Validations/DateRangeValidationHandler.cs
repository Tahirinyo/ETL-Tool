using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed class DateRangeValidationHandler : ISourceDateFormatValidationHandler
{
    private const string MinimumConfigurationKey = "Minimum";
    private const string MaximumConfigurationKey = "Maximum";

    public ValidationType Type => ValidationType.DateRange;

    public ValidationResult Validate(DataRow row, ValidationRule rule) =>
        throw new InvalidOperationException(
            "The date range validation requires the pipeline source culture and date format.");

    public ValidationResult Validate(
        DataRow row,
        ValidationRule rule,
        CultureInfo sourceCulture) =>
        Validate(row, rule, sourceCulture, dateFormat: null);

    public ValidationResult Validate(
        DataRow row,
        ValidationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            throw new InvalidOperationException(
                "A date range validation rule must specify a field.");
        }

        var range = ReadRange(rule, sourceCulture, dateFormat);

        if (!row.Values.TryGetValue(rule.Field, out var value)
            || value is null
            || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Valid(row);
        }

        if (value is DateTime date && range.Contains(date))
        {
            return ValidationResult.Valid(row);
        }

        var message = string.IsNullOrWhiteSpace(rule.ErrorMessage)
            ? CreateDefaultMessage(rule.Field, range)
            : rule.ErrorMessage;

        return ValidationResult.Invalid(row, new ValidationError(rule.Field, message));
    }

    private static DateRange ReadRange(
        ValidationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat)
    {
        if (rule.Configuration is null)
        {
            throw new InvalidOperationException(
                "A date range validation rule requires a configuration collection.");
        }

        var minimum = ReadBound(rule, sourceCulture, dateFormat, MinimumConfigurationKey);
        var maximum = ReadBound(rule, sourceCulture, dateFormat, MaximumConfigurationKey);

        if (minimum is null && maximum is null)
        {
            throw new InvalidOperationException(
                "A date range validation rule requires a 'Minimum' or 'Maximum' configuration value.");
        }

        if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
        {
            throw new InvalidOperationException(
                "A date range validation rule cannot have a 'Minimum' later than its 'Maximum'.");
        }

        return new DateRange(minimum, maximum);
    }

    internal static void ValidateConfiguration(
        ValidationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);
        _ = ReadRange(rule, sourceCulture, dateFormat);
    }

    private static DateTime? ReadBound(
        ValidationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat,
        string configurationKey)
    {
        if (!TryGetExactConfigurationValue(rule.Configuration, configurationKey, out var configured))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"The date range validation requires a non-empty '{configurationKey}' configuration value when it is supplied.");
        }

        try
        {
            return DateTextParser.Parse(
                configured,
                sourceCulture,
                dateFormat,
                $"The date range validation '{configurationKey}' configuration value");
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"The date range validation '{configurationKey}' configuration value is not a valid date for culture '{sourceCulture.Name}'.",
                exception);
        }
    }

    private static bool TryGetExactConfigurationValue(
        IReadOnlyDictionary<string, string> configuration,
        string configurationKey,
        out string? value)
    {
        var hasExactMatch = false;
        string? exactValue = null;

        foreach (var pair in configuration)
        {
            if (string.Equals(pair.Key, configurationKey, StringComparison.Ordinal))
            {
                hasExactMatch = true;
                exactValue = pair.Value;
            }
            else if (string.Equals(pair.Key, configurationKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The date range validation configuration key '{configurationKey}' must use exact ordinal casing.");
            }
        }

        value = exactValue;
        return hasExactMatch;
    }

    private static string CreateDefaultMessage(string field, DateRange range) =>
        range switch
        {
            { Minimum: not null, Maximum: not null } =>
                $"Field '{field}' must be between {FormatBound(range.Minimum.Value)} and {FormatBound(range.Maximum.Value)}.",
            { Minimum: not null } =>
                $"Field '{field}' must be at least {FormatBound(range.Minimum.Value)}.",
            _ => $"Field '{field}' must be at most {FormatBound(range.Maximum!.Value)}."
        };

    private static string FormatBound(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private readonly record struct DateRange(DateTime? Minimum, DateTime? Maximum)
    {
        public bool Contains(DateTime value) =>
            (!Minimum.HasValue || value >= Minimum.Value)
            && (!Maximum.HasValue || value <= Maximum.Value);
    }
}
