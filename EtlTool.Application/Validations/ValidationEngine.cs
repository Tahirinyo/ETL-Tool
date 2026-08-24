using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.ValueObjects;

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
        IReadOnlyCollection<ValidationRule> rules,
        SourceOptions sourceOptions)
    {
        ArgumentNullException.ThrowIfNull(transformedRow);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sourceOptions);

        var sourceCulture = sourceOptions.ResolveCulture();

        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException(
                    "The validation rule collection contains an invalid rule.",
                    nameof(rules));
            }

            var handler = _handlerRegistry.Resolve(rule.Type);
            var result = handler switch
            {
                ISourceCultureValidationHandler cultureAwareHandler => cultureAwareHandler.Validate(
                    transformedRow,
                    rule,
                    sourceCulture),
                _ => handler.Validate(transformedRow, rule)
            }
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
