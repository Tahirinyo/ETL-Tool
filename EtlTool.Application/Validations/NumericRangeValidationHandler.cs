using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed class NumericRangeValidationHandler : ISourceCultureValidationHandler
{
    private const string MinimumConfigurationKey = "Minimum";
    private const string MaximumConfigurationKey = "Maximum";

    public ValidationType Type => ValidationType.NumericRange;

    public ValidationResult Validate(DataRow row, ValidationRule rule) =>
        throw new InvalidOperationException(
            "The numeric range validation requires the pipeline source culture.");

    public ValidationResult Validate(
        DataRow row,
        ValidationRule rule,
        CultureInfo sourceCulture)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            throw new InvalidOperationException(
                "A numeric range validation rule must specify a field.");
        }

        var range = ReadRange(rule, sourceCulture);

        if (!row.Values.TryGetValue(rule.Field, out var value)
            || value is null
            || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Valid(row);
        }

        var isWithinRange = value switch
        {
            long number => range.Contains(number),
            decimal number => range.Contains(number),
            _ => false
        };

        if (isWithinRange)
        {
            return ValidationResult.Valid(row);
        }

        var message = string.IsNullOrWhiteSpace(rule.ErrorMessage)
            ? CreateDefaultMessage(rule.Field, range)
            : rule.ErrorMessage;

        return ValidationResult.Invalid(row, new ValidationError(rule.Field, message));
    }

    private static NumericRange ReadRange(ValidationRule rule, CultureInfo sourceCulture)
    {
        if (rule.Configuration is null)
        {
            throw new InvalidOperationException(
                "A numeric range validation rule requires a configuration collection.");
        }

        var minimum = ReadBound(rule, sourceCulture, MinimumConfigurationKey);
        var maximum = ReadBound(rule, sourceCulture, MaximumConfigurationKey);

        if (minimum is null && maximum is null)
        {
            throw new InvalidOperationException(
                "A numeric range validation rule requires a 'Minimum' or 'Maximum' configuration value.");
        }

        if (minimum.HasValue
            && maximum.HasValue
            && minimum.Value > maximum.Value)
        {
            throw new InvalidOperationException(
                "A numeric range validation rule cannot have a 'Minimum' greater than its 'Maximum'.");
        }

        return new NumericRange(minimum, maximum);
    }

    internal static void ValidateConfiguration(ValidationRule rule, CultureInfo sourceCulture)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);
        _ = ReadRange(rule, sourceCulture);
    }

    private static decimal? ReadBound(
        ValidationRule rule,
        CultureInfo sourceCulture,
        string configurationKey)
    {
        if (!TryGetExactConfigurationValue(rule.Configuration, configurationKey, out var configured))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"The numeric range validation requires a non-empty '{configurationKey}' configuration value when it is supplied.");
        }

        try
        {
            return ExactNumericText.Parse(configured, sourceCulture).ToDecimal();
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"The numeric range validation '{configurationKey}' configuration value is not a valid decimal value for culture '{sourceCulture.Name}'.",
                exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"The numeric range validation '{configurationKey}' configuration value cannot be represented exactly as a Decimal value.",
                exception);
        }
    }

    private static bool TryGetExactConfigurationValue(
        IReadOnlyDictionary<string, string> configuration,
        string configurationKey,
        out string? value)
    {
        foreach (var pair in configuration)
        {
            if (string.Equals(pair.Key, configurationKey, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }

            if (string.Equals(pair.Key, configurationKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The numeric range validation configuration key '{configurationKey}' must use exact ordinal casing.");
            }
        }

        value = null;
        return false;
    }

    private static string CreateDefaultMessage(string field, NumericRange range) =>
        range switch
        {
            { Minimum: not null, Maximum: not null } =>
                $"Field '{field}' must be between {FormatBound(range.Minimum.Value)} and {FormatBound(range.Maximum.Value)}.",
            { Minimum: not null } =>
                $"Field '{field}' must be at least {FormatBound(range.Minimum.Value)}.",
            _ => $"Field '{field}' must be at most {FormatBound(range.Maximum!.Value)}."
        };

    private static string FormatBound(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private readonly record struct NumericRange(decimal? Minimum, decimal? Maximum)
    {
        public bool Contains(decimal value) =>
            (!Minimum.HasValue || value >= Minimum.Value)
            && (!Maximum.HasValue || value <= Maximum.Value);
    }
}
