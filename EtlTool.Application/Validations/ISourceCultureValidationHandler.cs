using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Validations;

public interface ISourceCultureValidationHandler : IValidationHandler
{
    ValidationResult Validate(
        DataRow row,
        ValidationRule rule,
        CultureInfo sourceCulture);
}
