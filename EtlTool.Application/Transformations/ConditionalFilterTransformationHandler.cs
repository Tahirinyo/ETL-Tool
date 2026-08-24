using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ConditionalFilterTransformationHandler : ISourceDateFormatTransformationHandler
{
    private const string OperatorConfigurationKey = "Operator";
    private const string ValueConfigurationKey = "Value";

    public TransformationType Type => TransformationType.FilterRow;

    public TransformationResult Apply(DataRow row, TransformationRule rule) =>
        throw new InvalidOperationException(
            "The conditional filter transformation requires the pipeline source culture and date format.");

    public TransformationResult Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture) =>
        Apply(row, rule, sourceCulture, dateFormat: null);

    public TransformationResult Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The conditional filter transformation requires a non-empty source field.");
        }

        var filterOperator = ReadOperator(rule);
        var comparisonText = ReadComparisonValue(rule);

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The conditional filter transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is null)
        {
            return TransformationResult.Transformed(row);
        }

        var comparison = value switch
        {
            string text => string.CompareOrdinal(text, comparisonText),
            long number => number.CompareTo(ParseInt64(comparisonText, sourceCulture, rule, row)),
            decimal number => number.CompareTo(ParseDecimal(comparisonText, sourceCulture, rule, row)),
            DateTime date => date.CompareTo(DateTextParser.Parse(
                comparisonText,
                sourceCulture,
                dateFormat,
                $"The conditional filter comparison value for field '{rule.SourceField}' in row {row.SourceRowNumber}")),
            _ => throw new InvalidOperationException(
                $"The conditional filter transformation field '{rule.SourceField}' in row {row.SourceRowNumber} has unsupported value type '{value.GetType().FullName}'.")
        };

        return IsMatch(comparison, filterOperator)
            ? TransformationResult.Filtered(row)
            : TransformationResult.Transformed(row);
    }

    internal static FilterOperator ReadOperator(TransformationRule rule)
    {
        if (rule.Configuration is null
            || !rule.Configuration.TryGetValue(OperatorConfigurationKey, out var configured)
            || configured is null
            || !Enum.TryParse<FilterOperator>(configured, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed)
            || parsed == FilterOperator.Unspecified
            || !string.Equals(configured, parsed.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The conditional filter transformation requires a supported 'Operator' configuration value.");
        }

        return parsed;
    }

    internal static string ReadComparisonValue(TransformationRule rule)
    {
        if (rule.Configuration is null
            || !rule.Configuration.TryGetValue(ValueConfigurationKey, out var comparisonText)
            || comparisonText is null)
        {
            throw new InvalidOperationException(
                "The conditional filter transformation requires a non-null 'Value' configuration value.");
        }

        return comparisonText;
    }

    private static long ParseInt64(
        string comparisonText,
        CultureInfo sourceCulture,
        TransformationRule rule,
        DataRow row)
    {
        try
        {
            var parsed = ExactNumericText.Parse(comparisonText, sourceCulture);
            if (!parsed.IsMathematicallyIntegral)
            {
                throw new FormatException("The comparison value is not mathematically integral.");
            }

            return parsed.ToInt64();
        }
        catch (FormatException exception)
        {
            throw InvalidNumericComparison(rule, row, sourceCulture, "Int64", exception);
        }
        catch (OverflowException exception)
        {
            throw NumericComparisonOutOfRange(rule, row, sourceCulture, "Int64", exception);
        }
    }

    private static decimal ParseDecimal(
        string comparisonText,
        CultureInfo sourceCulture,
        TransformationRule rule,
        DataRow row)
    {
        try
        {
            return ExactNumericText.Parse(comparisonText, sourceCulture).ToDecimal();
        }
        catch (FormatException exception)
        {
            throw InvalidNumericComparison(rule, row, sourceCulture, "Decimal", exception);
        }
        catch (OverflowException exception)
        {
            throw NumericComparisonOutOfRange(rule, row, sourceCulture, "Decimal", exception);
        }
    }

    private static FormatException InvalidNumericComparison(
        TransformationRule rule,
        DataRow row,
        CultureInfo sourceCulture,
        string targetType,
        Exception innerException) => new(
            $"The conditional filter comparison value for field '{rule.SourceField}' in row {row.SourceRowNumber} is not a valid {targetType} value for culture '{sourceCulture.Name}'.",
            innerException);

    private static OverflowException NumericComparisonOutOfRange(
        TransformationRule rule,
        DataRow row,
        CultureInfo sourceCulture,
        string targetType,
        Exception innerException) => new(
            $"The conditional filter comparison value for field '{rule.SourceField}' in row {row.SourceRowNumber} cannot be represented exactly as a {targetType} value for culture '{sourceCulture.Name}'.",
            innerException);

    private static bool IsMatch(int comparison, FilterOperator filterOperator) => filterOperator switch
    {
        FilterOperator.Equals => comparison == 0,
        FilterOperator.NotEquals => comparison != 0,
        FilterOperator.GreaterThan => comparison > 0,
        FilterOperator.GreaterThanOrEqual => comparison >= 0,
        FilterOperator.LessThan => comparison < 0,
        FilterOperator.LessThanOrEqual => comparison <= 0,
        _ => throw new InvalidOperationException(
            $"The conditional filter operator '{filterOperator}' is not supported.")
    };
}
