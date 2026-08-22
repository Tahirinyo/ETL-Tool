using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Transformations;

public sealed class TransformationEngine
{
    private readonly TransformationHandlerRegistry _handlerRegistry;

    public TransformationEngine(TransformationHandlerRegistry handlerRegistry)
    {
        ArgumentNullException.ThrowIfNull(handlerRegistry);
        _handlerRegistry = handlerRegistry;
    }

    public TransformationResult Apply(
        DataRow mappedRow,
        IReadOnlyCollection<TransformationRule> rules,
        SourceOptions sourceOptions)
    {
        ArgumentNullException.ThrowIfNull(mappedRow);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sourceOptions);

        var sourceCulture = sourceOptions.ResolveCulture();

        if (rules.Count == 0)
        {
            return TransformationResult.Transformed(mappedRow);
        }

        var orders = new HashSet<int>();
        var materializedRules = rules.ToArray();

        foreach (var rule in materializedRules)
        {
            if (rule is null)
            {
                throw new ArgumentException(
                    "The transformation rule collection contains an invalid rule.",
                    nameof(rules));
            }

            if (!orders.Add(rule.Order))
            {
                throw new InvalidOperationException(
                    $"Transformation rule order '{rule.Order}' is used by more than one rule.");
            }
        }

        var executionSteps = materializedRules
            .OrderBy(rule => rule.Order)
            .Select(rule => new ExecutionStep(rule, _handlerRegistry.Resolve(rule.Type)))
            .ToArray();

        var currentRow = mappedRow;
        var currentResult = TransformationResult.Transformed(mappedRow);

        foreach (var step in executionSteps)
        {
            currentResult = step.Handler switch
            {
                ISourceDateFormatTransformationHandler dateFormatAwareHandler =>
                    dateFormatAwareHandler.Apply(
                        currentRow,
                        step.Rule,
                        sourceCulture,
                        sourceOptions.DateFormat),
                ISourceCultureTransformationHandler cultureAwareHandler =>
                    cultureAwareHandler.Apply(currentRow, step.Rule, sourceCulture),
                _ => step.Handler.Apply(currentRow, step.Rule)
            };

            currentResult = currentResult
                ?? throw new InvalidOperationException(
                    $"Transformation handler for type '{step.Handler.Type}' returned no result.");

            currentRow = currentResult.Row;
            if (currentResult.IsFiltered)
            {
                return currentResult;
            }
        }

        return currentResult;
    }

    private readonly record struct ExecutionStep(
        TransformationRule Rule,
        ITransformationHandler Handler);
}
