using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Transformations;

public sealed class TransformationExecution
{
    private readonly IReadOnlyList<TransformationExecutionStep> _steps;
    private readonly CultureInfo _sourceCulture;
    private readonly string? _dateFormat;
    private int _isApplying;

    internal TransformationExecution(
        IReadOnlyList<TransformationExecutionStep> steps,
        CultureInfo sourceCulture,
        string? dateFormat)
    {
        _steps = steps;
        _sourceCulture = sourceCulture;
        _dateFormat = dateFormat;
    }

    internal bool ContainsDeduplication =>
        _steps.Any(step => step.DeduplicationState is not null);

    public TransformationResult Apply(DataRow mappedRow)
    {
        return Execute(mappedRow, captureRowFailure: false).Result!;
    }

    internal TransformationExecutionOutcome ApplyForRowProcessing(DataRow mappedRow)
    {
        return Execute(mappedRow, captureRowFailure: true);
    }

    private TransformationExecutionOutcome Execute(
        DataRow mappedRow,
        bool captureRowFailure)
    {
        ArgumentNullException.ThrowIfNull(mappedRow);

        if (Interlocked.CompareExchange(ref _isApplying, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A transformation execution cannot process overlapping rows.");
        }

        try
        {
            return ApplyCore(mappedRow, captureRowFailure);
        }
        finally
        {
            Volatile.Write(ref _isApplying, 0);
        }
    }

    private TransformationExecutionOutcome ApplyCore(
        DataRow mappedRow,
        bool captureRowFailure)
    {
        var pendingKeys = new List<PendingDeduplicationKey>();
        var currentRow = mappedRow;
        var currentResult = TransformationResult.Transformed(mappedRow);

        foreach (var step in _steps)
        {
            try
            {
                if (step.DeduplicationState is not null)
                {
                    var handler = (DeduplicateTransformationHandler)step.Handler;
                    var evaluation = handler.Evaluate(currentRow, step.DeduplicationState);
                    if (evaluation.IsDuplicate)
                    {
                        return TransformationExecutionOutcome.Succeeded(
                            TransformationResult.Duplicate(currentRow));
                    }

                    pendingKeys.Add(new PendingDeduplicationKey(
                        step.DeduplicationState,
                        evaluation.Key));
                    currentResult = TransformationResult.Transformed(currentRow);
                }
                else
                {
                    currentResult = step.Handler switch
                    {
                        ISourceDateFormatTransformationHandler dateFormatAwareHandler =>
                            dateFormatAwareHandler.Apply(
                                currentRow,
                                step.Rule,
                                _sourceCulture,
                                _dateFormat),
                        ISourceCultureTransformationHandler cultureAwareHandler =>
                            cultureAwareHandler.Apply(currentRow, step.Rule, _sourceCulture),
                        _ => step.Handler.Apply(currentRow, step.Rule)
                    };
                }
            }
            catch (Exception exception)
                when (captureRowFailure && IsExpectedRowFailure(exception))
            {
                return TransformationExecutionOutcome.Failed(
                    currentRow,
                    step.Rule,
                    exception);
            }

            currentResult = currentResult
                ?? throw new InvalidOperationException(
                    $"Transformation handler for type '{step.Handler.Type}' returned no result.");

            currentRow = currentResult.Row;
            if (currentResult.IsFiltered || currentResult.IsDuplicate)
            {
                return TransformationExecutionOutcome.Succeeded(currentResult);
            }
        }

        foreach (var pendingKey in pendingKeys)
        {
            if (!pendingKey.State.Commit(pendingKey.Key))
            {
                throw new InvalidOperationException(
                    "A deduplication key changed while the current row was being transformed.");
            }
        }

        return TransformationExecutionOutcome.Succeeded(currentResult);
    }

    private static bool IsExpectedRowFailure(Exception exception) =>
        exception is FormatException or OverflowException or InvalidOperationException;

    private readonly record struct PendingDeduplicationKey(
        DeduplicationRuleExecutionState State,
        DeduplicationKey Key);
}

internal sealed class TransformationExecutionOutcome
{
    private TransformationExecutionOutcome(
        TransformationResult? result,
        DataRow row,
        TransformationRule? failedRule,
        Exception? failure)
    {
        Result = result;
        Row = row;
        FailedRule = failedRule;
        Failure = failure;
    }

    public TransformationResult? Result { get; }

    public DataRow Row { get; }

    public TransformationRule? FailedRule { get; }

    public Exception? Failure { get; }

    public bool IsSuccess => Result is not null;

    public static TransformationExecutionOutcome Succeeded(TransformationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new TransformationExecutionOutcome(result, result.Row, null, null);
    }

    public static TransformationExecutionOutcome Failed(
        DataRow row,
        TransformationRule rule,
        Exception failure)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(failure);
        return new TransformationExecutionOutcome(null, row, rule, failure);
    }
}

internal readonly record struct TransformationExecutionStep(
    TransformationRule Rule,
    ITransformationHandler Handler,
    DeduplicationRuleExecutionState? DeduplicationState);
