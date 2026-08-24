using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Validations;

public sealed class ValidationEngine
{
    private readonly ValidationHandlerRegistry _handlerRegistry;

    public ValidationEngine(ValidationHandlerRegistry handlerRegistry)
    {
        ArgumentNullException.ThrowIfNull(handlerRegistry);
        _handlerRegistry = handlerRegistry;
    }

    public ValidationResult Validate(
        DataRow transformedRow,
        IReadOnlyCollection<ValidationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(transformedRow);
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException(
                    "The validation rule collection contains an invalid rule.",
                    nameof(rules));
            }

            var result = _handlerRegistry.Resolve(rule.Type).Validate(transformedRow, rule)
                ?? throw new InvalidOperationException(
                    $"Validation handler for type '{rule.Type}' returned no result.");

            if (!result.IsValid)
            {
                return result;
            }
        }

        return ValidationResult.Valid(transformedRow);
    }
}
