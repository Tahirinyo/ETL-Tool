using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Transformations;

public sealed class TransformationEngine
{
    private readonly TransformationHandlerRegistry _handlerRegistry;

    public TransformationEngine(TransformationHandlerRegistry handlerRegistry)
    {
        ArgumentNullException.ThrowIfNull(handlerRegistry);
        _handlerRegistry = handlerRegistry;
    }

    public DataRow Apply(
        DataRow mappedRow,
        IReadOnlyCollection<TransformationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(mappedRow);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count == 0)
        {
            return mappedRow;
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

        foreach (var step in executionSteps)
        {
            currentRow = step.Handler.Apply(currentRow, step.Rule)
                ?? throw new InvalidOperationException(
                    $"Transformation handler for type '{step.Handler.Type}' returned no row.");
        }

        return currentRow;
    }

    private readonly record struct ExecutionStep(
        TransformationRule Rule,
        ITransformationHandler Handler);
}
