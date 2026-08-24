using System.Text.Json;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class DeduplicateTransformationHandler : ITransformationHandler
{
    private const string FieldsConfigurationKey = "Fields";

    public TransformationType Type => TransformationType.Deduplicate;

    public TransformationResult Apply(DataRow row, TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        throw new InvalidOperationException(
            "The deduplication transformation requires execution-scoped state.");
    }

    internal DeduplicationRuleExecutionState CreateExecutionState(TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new DeduplicationRuleExecutionState(ReadSelectedFields(rule));
    }

    internal static IReadOnlyList<string> ReadSelectedFields(TransformationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Configuration is null
            || !TryGetOrdinalConfigurationValue(
                rule.Configuration,
                FieldsConfigurationKey,
                out var configuredFields)
            || configuredFields is null)
        {
            throw new InvalidOperationException(
                "The deduplication transformation requires a non-null 'Fields' configuration value.");
        }

        string?[]? fields;
        try
        {
            fields = JsonSerializer.Deserialize<string?[]>(configuredFields);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The deduplication transformation requires 'Fields' to be a JSON string array.",
                exception);
        }

        if (fields is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                "The deduplication transformation requires at least one selected field.");
        }

        var selectedFields = new string[fields.Length];
        var uniqueFields = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new InvalidOperationException(
                    "The deduplication transformation field names cannot be empty or whitespace.");
            }

            if (!uniqueFields.Add(field))
            {
                throw new InvalidOperationException(
                    $"The deduplication transformation field '{field}' is selected more than once.");
            }

            selectedFields[index] = field;
        }

        return selectedFields;
    }

    internal DeduplicationEvaluation Evaluate(
        DataRow row,
        DeduplicationRuleExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(state);

        var components = new DeduplicationKeyComponent[state.SelectedFields.Count];

        for (var index = 0; index < state.SelectedFields.Count; index++)
        {
            var field = state.SelectedFields[index];
            if (!row.Values.TryGetValue(field, out var value))
            {
                throw new InvalidOperationException(
                    $"The deduplication transformation field '{field}' is missing from row {row.SourceRowNumber}.");
            }

            components[index] = DeduplicationKeyComponent.Create(value, field, row.SourceRowNumber);
        }

        var key = new DeduplicationKey(components);
        return new DeduplicationEvaluation(key, state.Contains(key));
    }

    private static bool TryGetOrdinalConfigurationValue(
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

internal sealed class DeduplicationRuleExecutionState
{
    private readonly HashSet<DeduplicationKey> _seenKeys = [];

    public DeduplicationRuleExecutionState(IReadOnlyList<string> selectedFields)
    {
        SelectedFields = selectedFields;
    }

    public IReadOnlyList<string> SelectedFields { get; }

    public bool Contains(DeduplicationKey key) => _seenKeys.Contains(key);

    public bool Commit(DeduplicationKey key) => _seenKeys.Add(key);
}

internal readonly record struct DeduplicationEvaluation(
    DeduplicationKey Key,
    bool IsDuplicate);

internal sealed class DeduplicationKey : IEquatable<DeduplicationKey>
{
    private readonly DeduplicationKeyComponent[] _components;
    private readonly int _hashCode;

    public DeduplicationKey(DeduplicationKeyComponent[] components)
    {
        _components = components;

        var hash = new HashCode();
        foreach (var component in components)
        {
            component.AddToHash(ref hash);
        }

        _hashCode = hash.ToHashCode();
    }

    public bool Equals(DeduplicationKey? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || _components.Length != other._components.Length) return false;

        for (var index = 0; index < _components.Length; index++)
        {
            if (!_components[index].Equals(other._components[index])) return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DeduplicationKey);

    public override int GetHashCode() => _hashCode;
}

internal readonly struct DeduplicationKeyComponent : IEquatable<DeduplicationKeyComponent>
{
    private DeduplicationKeyComponent(Type? runtimeType, object? value)
    {
        RuntimeType = runtimeType;
        Value = value;
    }

    private Type? RuntimeType { get; }

    private object? Value { get; }

    public static DeduplicationKeyComponent Create(
        object? value,
        string field,
        long sourceRowNumber)
    {
        if (value is null)
        {
            return new DeduplicationKeyComponent(null, null);
        }

        if (value is not string
            and not bool
            and not sbyte
            and not byte
            and not short
            and not ushort
            and not int
            and not uint
            and not long
            and not ulong
            and not float
            and not double
            and not decimal
            and not DateTime
            and not DateTimeOffset)
        {
            throw new InvalidOperationException(
                $"The deduplication transformation field '{field}' in row {sourceRowNumber} has unsupported value type '{value.GetType().FullName}'.");
        }

        return new DeduplicationKeyComponent(value.GetType(), value);
    }

    public bool Equals(DeduplicationKeyComponent other)
    {
        if (RuntimeType != other.RuntimeType) return false;
        if (Value is string text && other.Value is string otherText)
        {
            return StringComparer.Ordinal.Equals(text, otherText);
        }

        return Equals(Value, other.Value);
    }

    public override bool Equals(object? obj) =>
        obj is DeduplicationKeyComponent other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        AddToHash(ref hash);
        return hash.ToHashCode();
    }

    public void AddToHash(ref HashCode hash)
    {
        hash.Add(RuntimeType);
        if (Value is string text)
        {
            hash.Add(text, StringComparer.Ordinal);
        }
        else
        {
            hash.Add(Value);
        }
    }
}
