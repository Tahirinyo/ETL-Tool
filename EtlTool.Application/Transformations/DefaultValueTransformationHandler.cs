using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class DefaultValueTransformationHandler : ITransformationHandler
{
    private const string DefaultValueConfigurationKey = "Value";

    public TransformationType Type => TransformationType.SetDefaultValue;

    public TransformationResult Apply(DataRow row, TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The default value transformation requires a non-empty source field.");
        }

        if (rule.Configuration is null
            || !rule.Configuration.TryGetValue(DefaultValueConfigurationKey, out var defaultValue)
            || defaultValue is null)
        {
            throw new InvalidOperationException(
                "The default value transformation requires a non-null 'Value' configuration value.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The default value transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is null or "")
        {
            row.Values[rule.SourceField] = defaultValue;
        }

        return TransformationResult.Transformed(row);
    }
}
