using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Validations;

public interface IValidationHandler
{
    ValidationType Type { get; }

    ValidationResult Validate(DataRow row, ValidationRule rule);
}
