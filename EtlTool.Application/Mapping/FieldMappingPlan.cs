namespace EtlTool.Application.Mapping;

/// <summary>
/// Represents a validated, immutable snapshot of a pipeline's active field mappings.
/// </summary>
public sealed class FieldMappingPlan
{
    private readonly MappingEntry[] _mappings;

    internal FieldMappingPlan(MappingEntry[] mappings)
    {
        _mappings = mappings;
    }

    internal ReadOnlySpan<MappingEntry> Mappings => _mappings;

    internal readonly record struct MappingEntry(
        string SourceField,
        string TargetField);
}
