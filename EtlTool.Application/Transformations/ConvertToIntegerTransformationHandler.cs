using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ConvertToIntegerTransformationHandler : ISourceCultureTransformationHandler
{
    public TransformationType Type => TransformationType.ConvertToInteger;

    public TransformationResult Apply(DataRow row, TransformationRule rule) =>
        throw new InvalidOperationException(
            "The convert to integer transformation requires the pipeline source culture.");

    public TransformationResult Apply(
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
                "The convert to integer transformation requires a non-empty source field.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The convert to integer transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is null)
        {
            return TransformationResult.Transformed(row);
        }

        var converted = value switch
        {
            sbyte number => (long)number,
            byte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => ConvertUnsignedInteger(number, rule.SourceField, row.SourceRowNumber),
            decimal number => ConvertDecimal(number, rule.SourceField, row.SourceRowNumber),
            double number => ConvertDouble(number, rule.SourceField, row.SourceRowNumber),
            float number => ConvertSingle(number, rule.SourceField, row.SourceRowNumber),
            string text => ConvertText(text, sourceCulture, rule.SourceField, row.SourceRowNumber),
            _ => throw UnsupportedValue(value, rule.SourceField, row.SourceRowNumber)
        };

        row.Values[rule.SourceField] = converted;
        return TransformationResult.Transformed(row);
    }

    private static long ConvertText(
        string text,
        CultureInfo sourceCulture,
        string field,
        long rowNumber)
    {
        ExactNumericText number;

        try
        {
            number = ExactNumericText.Parse(text, sourceCulture);
        }
        catch (FormatException exception)
        {
            throw new FormatException(
                $"The convert to integer transformation field '{field}' in row {rowNumber} is not a valid number for culture '{sourceCulture.Name}'.",
                exception);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException(
                $"The convert to integer transformation field '{field}' in row {rowNumber} is outside the supported numeric range.",
                exception);
        }

        if (!number.IsMathematicallyIntegral)
        {
            throw FractionalValue(field, rowNumber);
        }

        try
        {
            return number.ToInt64();
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }
    }

    private static long ConvertUnsignedInteger(ulong number, string field, long rowNumber)
    {
        try
        {
            return checked((long)number);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }
    }

    private static long ConvertDecimal(decimal number, string field, long rowNumber)
    {
        if (decimal.Truncate(number) != number)
        {
            throw FractionalValue(field, rowNumber);
        }

        try
        {
            return checked((long)number);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }
    }

    private static long ConvertDouble(double number, string field, long rowNumber)
    {
        if (!double.IsFinite(number))
        {
            throw OutOfRange(field, rowNumber);
        }

        if (Math.Truncate(number) != number)
        {
            throw FractionalValue(field, rowNumber);
        }

        try
        {
            return checked((long)number);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }
    }

    private static long ConvertSingle(float number, string field, long rowNumber)
    {
        if (!float.IsFinite(number))
        {
            throw OutOfRange(field, rowNumber);
        }

        if (MathF.Truncate(number) != number)
        {
            throw FractionalValue(field, rowNumber);
        }

        try
        {
            return checked((long)number);
        }
        catch (OverflowException exception)
        {
            throw OutOfRange(field, rowNumber, exception);
        }
    }

    private static FormatException FractionalValue(string field, long rowNumber) => new(
        $"The convert to integer transformation field '{field}' in row {rowNumber} must contain a mathematically integral value.");

    private static OverflowException OutOfRange(
        string field,
        long rowNumber,
        Exception? innerException = null) => new(
            $"The convert to integer transformation field '{field}' in row {rowNumber} is outside the Int64 range.",
            innerException);

    private static InvalidOperationException UnsupportedValue(
        object value,
        string field,
        long rowNumber) => new(
            $"The convert to integer transformation field '{field}' in row {rowNumber} has unsupported value type '{value.GetType().FullName}'.");
}
