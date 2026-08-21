using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ToLowerTransformationHandler : ITransformationHandler
{
    public TransformationType Type => TransformationType.ToLower;

    public DataRow Apply(DataRow row, TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The to lower transformation requires a non-empty source field.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The to lower transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is string text)
        {
            row.Values[rule.SourceField] = text.ToLowerInvariant();
        }

        return row;
    }
}
