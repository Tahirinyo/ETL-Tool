using System.ComponentModel.DataAnnotations;
using EtlTool.Domain.Enums;

namespace EtlTool.Web.Models.Pipelines;

public sealed class TransformationRuleFormViewModel : IValidatableObject
{
    public TransformationType Type { get; set; }

    public string? SourceField { get; set; }

    public List<string> AvailableMappedFields { get; set; } = [];

    public List<string> SelectedFields { get; set; } = [];

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? DefaultValue { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Find { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Replace { get; set; }

    public FilterOperator FilterOperator { get; set; }

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? FilterValue { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type is not TransformationType.Trim
            and not TransformationType.ToUpper
            and not TransformationType.ToLower
            and not TransformationType.ConvertToString
            and not TransformationType.ConvertToInteger
            and not TransformationType.ConvertToDecimal
            and not TransformationType.ConvertToDate
            and not TransformationType.SetDefaultValue
            and not TransformationType.FilterRow
            and not TransformationType.FindAndReplace
            and not TransformationType.Deduplicate)
        {
            yield return new ValidationResult("Choose a supported transformation type.", [nameof(Type)]);
        }

        if (Type != TransformationType.Deduplicate && string.IsNullOrWhiteSpace(SourceField))
        {
            yield return new ValidationResult("Choose a mapped output field.", [nameof(SourceField)]);
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

        if (Type == TransformationType.FilterRow)
        {
            if (!Enum.IsDefined(FilterOperator) || FilterOperator == FilterOperator.Unspecified)
            {
                yield return new ValidationResult("Choose a supported filter operator.", [nameof(FilterOperator)]);
            }

            if (FilterValue is null)
            {
                yield return new ValidationResult("A filter comparison value is required.", [nameof(FilterValue)]);
            }
        }

        if (Type == TransformationType.Deduplicate && SelectedFields.Count == 0)
        {
            yield return new ValidationResult("Choose at least one deduplication field.", [nameof(SelectedFields)]);
        }
    }
}
