using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ConvertToStringTransformationHandler : ITransformationHandler
{
    public TransformationType Type => TransformationType.ConvertToString;

    public TransformationResult Apply(DataRow row, TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The convert to string transformation requires a non-empty source field.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The convert to string transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is null or string)
        {
            return TransformationResult.Transformed(row);
        }

        if (value is bool boolean)
        {
            row.Values[rule.SourceField] = boolean.ToString();
            return TransformationResult.Transformed(row);
        }

        if (value is IFormattable formattable && IsSupportedFormattableValue(value))
        {
            row.Values[rule.SourceField] = formattable.ToString(null, CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException(
                    $"The convert to string transformation field '{rule.SourceField}' in row {row.SourceRowNumber} produced no string value.");
            return TransformationResult.Transformed(row);
        }

        throw new InvalidOperationException(
            $"The convert to string transformation field '{rule.SourceField}' in row {row.SourceRowNumber} has unsupported value type '{value.GetType().FullName}'.");
    }

    private static bool IsSupportedFormattableValue(object value) => value is
        sbyte or byte or short or ushort or int or uint or long or ulong
        or float or double or decimal
        or DateTime or DateTimeOffset;
}
