using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed class RequiredValidationHandler : IValidationHandler
{
    public ValidationType Type => ValidationType.Required;

    public ValidationResult Validate(DataRow row, ValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            throw new InvalidOperationException("A required validation rule must specify a field.");
        }

        if (row.Values.TryGetValue(rule.Field, out var value)
            && value is not null
            && (value is not string text || !string.IsNullOrWhiteSpace(text)))
        {
            return ValidationResult.Valid(row);
        }

        var message = string.IsNullOrWhiteSpace(rule.ErrorMessage)
            ? $"Field '{rule.Field}' is required."
            : rule.ErrorMessage;

        return ValidationResult.Invalid(row, new ValidationError(rule.Field, message));
    }
}
