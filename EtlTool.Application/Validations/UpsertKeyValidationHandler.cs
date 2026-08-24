using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public sealed class UpsertKeyValidationHandler : IValidationHandler
{
    public ValidationType Type => ValidationType.UpsertKeyRequired;

    public ValidationResult Validate(DataRow row, ValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            throw new InvalidOperationException(
                "An upsert-key validation rule must specify a field.");
        }

        if (row.Values.TryGetValue(rule.Field, out var value)
            && ValidationValuePresence.IsPresent(value))
        {
            return ValidationResult.Valid(row);
        }

        var message = string.IsNullOrWhiteSpace(rule.ErrorMessage)
            ? $"Upsert key field '{rule.Field}' is required."
            : rule.ErrorMessage;

        return ValidationResult.Invalid(
            row,
            new ValidationError(rule.Field, message));
    }
}
