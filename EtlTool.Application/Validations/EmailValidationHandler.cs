using System.ComponentModel.DataAnnotations;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed class EmailValidationHandler : IValidationHandler
{
    private static readonly EmailAddressAttribute EmailAddressValidator = new();

    public ValidationType Type => ValidationType.EmailFormat;

    public ValidationResult Validate(DataRow row, ValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            throw new InvalidOperationException("An email validation rule must specify a field.");
        }

        if (!row.Values.TryGetValue(rule.Field, out var value)
            || value is null
            || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Valid(row);
        }

        if (value is string email && EmailAddressValidator.IsValid(email))
        {
            return ValidationResult.Valid(row);
        }

        var message = string.IsNullOrWhiteSpace(rule.ErrorMessage)
            ? $"Field '{rule.Field}' must be a valid email address."
            : rule.ErrorMessage;

        return ValidationResult.Invalid(row, new ValidationError(rule.Field, message));
    }
}
