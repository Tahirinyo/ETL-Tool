using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class FindAndReplaceTransformationHandler : ITransformationHandler
{
    private const string FindConfigurationKey = "Find";
    private const string ReplaceConfigurationKey = "Replace";

    public TransformationType Type => TransformationType.FindAndReplace;

    public TransformationResult Apply(DataRow row, TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The find and replace transformation requires a non-empty source field.");
        }

        if (rule.Configuration is null
            || !TryGetConfigurationValue(rule.Configuration, FindConfigurationKey, out var find)
            || string.IsNullOrEmpty(find))
        {
            throw new InvalidOperationException(
                "The find and replace transformation requires a non-empty 'Find' configuration value.");
        }

        if (!TryGetConfigurationValue(rule.Configuration, ReplaceConfigurationKey, out var replace)
            || replace is null)
        {
            throw new InvalidOperationException(
                "The find and replace transformation requires a non-null 'Replace' configuration value.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The find and replace transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is string text)
        {
            row.Values[rule.SourceField] = text.Replace(find, replace, StringComparison.Ordinal);
        }

        return TransformationResult.Transformed(row);
    }

    private static bool TryGetConfigurationValue(
        Dictionary<string, string> configuration,
        string key,
        out string? value)
    {
        foreach (var entry in configuration)
        {
            if (StringComparer.Ordinal.Equals(entry.Key, key))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
