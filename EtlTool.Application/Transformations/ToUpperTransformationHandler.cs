using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ToUpperTransformationHandler : ITransformationHandler
{
    public TransformationType Type => TransformationType.ToUpper;

    public TransformationResult Apply(DataRow row, TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The to upper transformation requires a non-empty source field.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The to upper transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is string text)
        {
            row.Values[rule.SourceField] = text.ToUpperInvariant();
        }

        return TransformationResult.Transformed(row);
    }
}
