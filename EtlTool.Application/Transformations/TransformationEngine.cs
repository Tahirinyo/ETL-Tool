using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
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

        var execution = CreateExecution(rules, sourceOptions);
        if (execution.ContainsDeduplication)
        {
            throw new InvalidOperationException(
                "Deduplication rules require a reusable transformation execution created with CreateExecution.");
        }

        return execution.Apply(mappedRow);
    }

    public TransformationExecution CreateExecution(
        IReadOnlyCollection<TransformationRule> rules,
        SourceOptions sourceOptions)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sourceOptions);

        var sourceCulture = sourceOptions.ResolveCulture();

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
            .Select(CreateExecutionStep)
            .ToArray();

        return new TransformationExecution(
            executionSteps,
            sourceCulture,
            sourceOptions.DateFormat);
    }

    private TransformationExecutionStep CreateExecutionStep(TransformationRule rule)
    {
        var handler = _handlerRegistry.Resolve(rule.Type);
        DeduplicationRuleExecutionState? deduplicationState = null;

        if (rule.Type == TransformationType.Deduplicate)
        {
            if (handler is not DeduplicateTransformationHandler deduplicationHandler)
            {
                throw new InvalidOperationException(
                    "The registered deduplication transformation handler does not support execution-scoped state.");
            }

            deduplicationState = deduplicationHandler.CreateExecutionState(rule);
        }

        return new TransformationExecutionStep(rule, handler, deduplicationState);
    }
}
