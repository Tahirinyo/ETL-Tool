using System.ComponentModel.DataAnnotations;
using EtlTool.Domain.Enums;

namespace EtlTool.Web.Models.Pipelines;

public sealed class ValidationRuleFormViewModel : IValidatableObject
{
    public ValidationType Type { get; set; }

    public string? Field { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Minimum { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Maximum { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? ErrorMessage { get; set; }

    public List<string> AvailableMappedFields { get; set; } = [];

    public string? UpsertKeyField { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type is not ValidationType.Required
            and not ValidationType.EmailFormat
            and not ValidationType.NumericRange
            and not ValidationType.TextLengthRange
            and not ValidationType.DateRange
            and not ValidationType.UpsertKeyRequired)
        {
            yield return new ValidationResult("Choose a supported validation type.", [nameof(Type)]);
            yield break;
        }

        if (Type != ValidationType.UpsertKeyRequired && string.IsNullOrWhiteSpace(Field))
        {
            yield return new ValidationResult("Choose a mapped output field.", [nameof(Field)]);
        }

        if (Type is ValidationType.NumericRange or ValidationType.TextLengthRange or ValidationType.DateRange
            && string.IsNullOrWhiteSpace(Minimum)
            && string.IsNullOrWhiteSpace(Maximum))
        {
            yield return new ValidationResult("Enter a minimum or maximum value.", [nameof(Minimum), nameof(Maximum)]);
        }

        if (Type == ValidationType.TextLengthRange)
        {
            if (!IsNonNegativeInteger(Minimum)) yield return new ValidationResult("Minimum must be a non-negative whole number.", [nameof(Minimum)]);
            if (!IsNonNegativeInteger(Maximum)) yield return new ValidationResult("Maximum must be a non-negative whole number.", [nameof(Maximum)]);
            if (TryReadLength(Minimum, out var minimum) && TryReadLength(Maximum, out var maximum) && minimum > maximum)
            {
                yield return new ValidationResult("Minimum cannot be greater than maximum.", [nameof(Minimum), nameof(Maximum)]);
            }
        }
    }

    private static bool IsNonNegativeInteger(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed >= 0;

    private static bool TryReadLength(string? value, out int parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
            && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed);
    }
}
