using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ConvertToDecimalTransformationHandler : ISourceCultureTransformationHandler
{
    public TransformationType Type => TransformationType.ConvertToDecimal;

    public DataRow Apply(DataRow row, TransformationRule rule) =>
        throw new InvalidOperationException(
            "The convert to decimal transformation requires the pipeline source culture.");

    public DataRow Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The convert to decimal transformation requires a non-empty source field.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The convert to decimal transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is null)
        {
            return row;
        }

        var converted = value switch
        {
            sbyte number => (decimal)number,
            byte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            decimal number => number,
            double number => ConvertDouble(number, rule.SourceField, row.SourceRowNumber),
            float number => ConvertSingle(number, rule.SourceField, row.SourceRowNumber),
            string text => ConvertText(text, sourceCulture, rule.SourceField, row.SourceRowNumber),
            _ => throw UnsupportedValue(value, rule.SourceField, row.SourceRowNumber)
        };

        row.Values[rule.SourceField] = converted;
        return row;
    }

    private static decimal ConvertText(
        string text,
        CultureInfo sourceCulture,
        string field,
        long rowNumber)
    {
        try
        {
            return ExactNumericText.Parse(text, sourceCulture).ToDecimal();
        }
        catch (FormatException exception)
        {
            throw new FormatException(
                $"The convert to decimal transformation field '{field}' in row {rowNumber} is not a valid number for culture '{sourceCulture.Name}'.",
                exception);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }
    }

    private static decimal ConvertDouble(double number, string field, long rowNumber)
    {
        if (!double.IsFinite(number))
        {
            throw OutOfRange(field, rowNumber);
        }

        decimal converted;
        try
        {
            converted = checked((decimal)number);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }

        return (double)converted != number
            ? throw OutOfRange(field, rowNumber)
            : converted;
    }

    private static decimal ConvertSingle(float number, string field, long rowNumber)
    {
        if (!float.IsFinite(number))
        {
            throw OutOfRange(field, rowNumber);
        }

        decimal converted;
        try
        {
            converted = checked((decimal)number);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }

        return (float)converted != number
            ? throw OutOfRange(field, rowNumber)
            : converted;
    }

    private static OverflowException OutOfRange(
        string field,
        long rowNumber,
        Exception? innerException = null) => new(
            $"The convert to decimal transformation field '{field}' in row {rowNumber} is outside the Decimal range.",
            innerException);

    private static InvalidOperationException UnsupportedValue(
        object value,
        string field,
        long rowNumber) => new(
            $"The convert to decimal transformation field '{field}' in row {rowNumber} has unsupported value type '{value.GetType().FullName}'.");
}
