using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed class TextLengthValidationHandler : IValidationHandler
{
    private const string MinimumConfigurationKey = "Minimum";
    private const string MaximumConfigurationKey = "Maximum";

    public ValidationType Type => ValidationType.TextLengthRange;

    public ValidationResult Validate(DataRow row, ValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            throw new InvalidOperationException(
                "A text length validation rule must specify a field.");
        }

        var range = ReadRange(rule);

        if (!row.Values.TryGetValue(rule.Field, out var value)
            || value is null
            || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Valid(row);
        }

        if (value is string textValue && range.Contains(textValue.Length))
        {
            return ValidationResult.Valid(row);
        }

        var message = string.IsNullOrWhiteSpace(rule.ErrorMessage)
            ? CreateDefaultMessage(rule.Field, range)
            : rule.ErrorMessage;

        return ValidationResult.Invalid(row, new ValidationError(rule.Field, message));
    }

    private static TextLengthRange ReadRange(ValidationRule rule)
    {
        if (rule.Configuration is null)
        {
            throw new InvalidOperationException(
                "A text length validation rule requires a configuration collection.");
        }

        var minimum = ReadBound(rule, MinimumConfigurationKey);
        var maximum = ReadBound(rule, MaximumConfigurationKey);

        if (minimum is null && maximum is null)
        {
            throw new InvalidOperationException(
                "A text length validation rule requires a 'Minimum' or 'Maximum' configuration value.");
        }

        if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
        {
            throw new InvalidOperationException(
                "A text length validation rule cannot have a 'Minimum' greater than its 'Maximum'.");
        }

        return new TextLengthRange(minimum, maximum);
    }

    internal static void ValidateConfiguration(ValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _ = ReadRange(rule);
    }

    private static int? ReadBound(ValidationRule rule, string configurationKey)
    {
        if (!TryGetExactConfigurationValue(rule.Configuration, configurationKey, out var configured))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"The text length validation requires a non-empty '{configurationKey}' configuration value when it is supplied.");
        }

        if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var bound)
            || bound < 0)
        {
            throw new InvalidOperationException(
                $"The text length validation '{configurationKey}' configuration value must be a non-negative whole number.");
        }

        return bound;
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
                    $"The text length validation configuration key '{configurationKey}' must use exact ordinal casing.");
            }
        }

        value = null;
        return false;
    }

    private static string CreateDefaultMessage(string field, TextLengthRange range) =>
        range switch
        {
            { Minimum: not null, Maximum: not null } =>
                $"Field '{field}' must be between {range.Minimum.Value} and {range.Maximum.Value} characters.",
            { Minimum: not null } =>
                $"Field '{field}' must be at least {range.Minimum.Value} characters.",
            _ => $"Field '{field}' must be at most {range.Maximum!.Value} characters."
        };

    private readonly record struct TextLengthRange(int? Minimum, int? Maximum)
    {
        public bool Contains(int length) =>
            (!Minimum.HasValue || length >= Minimum.Value)
            && (!Maximum.HasValue || length <= Maximum.Value);
    }
}
