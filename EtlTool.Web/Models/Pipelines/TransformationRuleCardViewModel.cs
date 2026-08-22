using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using System.Text.Json;

namespace EtlTool.Web.Models.Pipelines;

public sealed class TransformationRuleCardViewModel
{
    public Guid Id { get; init; }

    public int Order { get; init; }

    public string TypeLabel { get; init; } = string.Empty;

    public string TargetField { get; init; } = string.Empty;

    public IReadOnlyList<TransformationRuleConfigurationViewModel> Configuration { get; init; } = [];

    public static TransformationRuleCardViewModel FromRule(TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var configuration = rule.Configuration;
        return new TransformationRuleCardViewModel
        {
            Id = rule.Id,
            Order = rule.Order,
            TypeLabel = GetTypeLabel(rule.Type),
            TargetField = string.IsNullOrWhiteSpace(rule.SourceField)
                ? "Unavailable"
                : rule.SourceField,
            Configuration = GetConfiguration(rule.Type, configuration)
        };
    }

    private static string GetTypeLabel(TransformationType type) => type switch
    {
        TransformationType.Trim => "Trim",
        TransformationType.ToUpper => "Convert to uppercase",
        TransformationType.ToLower => "Convert to lowercase",
        TransformationType.ConvertToString => "Convert to string",
        TransformationType.ConvertToInteger => "Convert to integer",
        TransformationType.ConvertToDecimal => "Convert to decimal",
        TransformationType.ConvertToDate => "Convert to date",
        TransformationType.SetDefaultValue => "Set default value",
        TransformationType.FilterRow => "Conditional filter",
        TransformationType.FindAndReplace => "Find and replace",
        TransformationType.Deduplicate => "Deduplicate",
        _ => "Unsupported transformation"
    };

    private static IReadOnlyList<TransformationRuleConfigurationViewModel> GetConfiguration(
        TransformationType type,
        Dictionary<string, string>? configuration)
    {
        return type switch
        {
            TransformationType.Trim
                or TransformationType.ToUpper
                or TransformationType.ToLower
                or TransformationType.ConvertToString
                or TransformationType.ConvertToInteger
                or TransformationType.ConvertToDecimal
                or TransformationType.ConvertToDate =>
                [new("Configuration", "No additional configuration", false)],
            TransformationType.SetDefaultValue =>
                [CreateValue("Default", configuration, "Value")],
            TransformationType.FilterRow =>
            [
                CreateValue("Operator", configuration, "Operator"),
                CreateValue("Comparison value", configuration, "Value")
            ],
            TransformationType.FindAndReplace =>
            [
                CreateValue("Find", configuration, "Find"),
                CreateValue("Replace", configuration, "Replace")
            ],
            TransformationType.Deduplicate => [CreateFieldsValue(configuration)],
            _ => [new("Configuration", "Configuration unavailable", true)]
        };
    }

    private static TransformationRuleConfigurationViewModel CreateFieldsValue(
        Dictionary<string, string>? configuration)
    {
        if (configuration is null
            || !configuration.TryGetValue("Fields", out var configuredFields)
            || configuredFields is null)
        {
            return new("Fields", "Configuration unavailable", true);
        }

        try
        {
            var fields = JsonSerializer.Deserialize<string?[]>(configuredFields);
            if (fields is not { Length: > 0 } || fields.Any(string.IsNullOrWhiteSpace))
            {
                return new("Fields", "Configuration unavailable", true);
            }

            return new("Fields", string.Join(", ", fields!), false);
        }
        catch (JsonException)
        {
            return new("Fields", "Configuration unavailable", true);
        }
    }

    private static TransformationRuleConfigurationViewModel CreateValue(
        string label,
        Dictionary<string, string>? configuration,
        string key)
    {
        if (configuration is null || !configuration.TryGetValue(key, out var value))
        {
            return new(label, "Configuration unavailable", true);
        }

        if (value is null)
        {
            return new(label, "Null value", true);
        }

        if (value.Length == 0)
        {
            return new(label, "Empty string (\"\")", false);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return new(label, $"Whitespace-only value (length: {value.Length})", false);
        }

        return new(label, value, false);
    }
}

public sealed record TransformationRuleConfigurationViewModel(
    string Label,
    string Value,
    bool IsWarning);
