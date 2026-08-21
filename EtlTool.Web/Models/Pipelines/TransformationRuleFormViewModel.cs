using System.ComponentModel.DataAnnotations;
using EtlTool.Domain.Enums;

namespace EtlTool.Web.Models.Pipelines;

public sealed class TransformationRuleFormViewModel : IValidatableObject
{
    public TransformationType Type { get; set; }

    [Required]
    public string SourceField { get; set; } = string.Empty;

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? DefaultValue { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Find { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Replace { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type is not TransformationType.Trim
            and not TransformationType.ToUpper
            and not TransformationType.ToLower
            and not TransformationType.SetDefaultValue
            and not TransformationType.FindAndReplace)
        {
            yield return new ValidationResult("Choose a supported transformation type.", [nameof(Type)]);
        }

        if (Type == TransformationType.SetDefaultValue && DefaultValue is null)
        {
            yield return new ValidationResult("A default value is required.", [nameof(DefaultValue)]);
        }

        if (Type == TransformationType.FindAndReplace)
        {
            if (string.IsNullOrEmpty(Find))
            {
                yield return new ValidationResult("A find value is required.", [nameof(Find)]);
            }
            if (Replace is null)
            {
                yield return new ValidationResult("A replace value is required.", [nameof(Replace)]);
            }
        }
    }
}
