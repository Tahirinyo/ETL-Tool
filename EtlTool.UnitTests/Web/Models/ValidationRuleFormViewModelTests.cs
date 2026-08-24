using System.ComponentModel.DataAnnotations;
using EtlTool.Domain.Enums;
using EtlTool.Web.Models.Pipelines;

namespace EtlTool.UnitTests.Web.Models;

public sealed class ValidationRuleFormViewModelTests
{
    [Fact]
    public void Validate_AcceptsFieldRulesAndOneSidedRanges()
    {
        Assert.Empty(Validate(new ValidationRuleFormViewModel { Type = ValidationType.Required, Field = "name" }));
        Assert.Empty(Validate(new ValidationRuleFormViewModel { Type = ValidationType.NumericRange, Field = "amount", Minimum = "1" }));
    }

    [Fact]
    public void Validate_RejectsUnsupportedTypeMissingFieldAndInvalidTextRange()
    {
        var invalid = Validate(new ValidationRuleFormViewModel { Type = ValidationType.TextLengthRange, Field = "", Minimum = "10", Maximum = "2" });
        Assert.Contains(invalid, result => result.MemberNames.Contains(nameof(ValidationRuleFormViewModel.Field)));
        Assert.Contains(invalid, result => result.MemberNames.Contains(nameof(ValidationRuleFormViewModel.Minimum)));
        Assert.Contains(Validate(new ValidationRuleFormViewModel { Type = (ValidationType)999 }), result => result.MemberNames.Contains(nameof(ValidationRuleFormViewModel.Type)));
    }

    private static IReadOnlyList<ValidationResult> Validate(ValidationRuleFormViewModel model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }
}
