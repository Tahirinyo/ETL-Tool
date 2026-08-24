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
        IReadOnlyList<ValidationRule> rules,
        SourceOptions sourceOptions)
    {
        ArgumentNullException.ThrowIfNull(transformedRow);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sourceOptions);

        var sourceCulture = sourceOptions.ResolveCulture();
        List<ValidationError> errors = [];

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
                ISourceDateFormatValidationHandler dateFormatAwareHandler => dateFormatAwareHandler.Validate(
                    transformedRow,
                    rule,
                    sourceCulture,
                    sourceOptions.DateFormat),
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
                errors.AddRange(result.Errors);
            }
        }

        return errors.Count == 0
            ? ValidationResult.Valid(transformedRow)
            : ValidationResult.Invalid(transformedRow, errors);
    }
}
