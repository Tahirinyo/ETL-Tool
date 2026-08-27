using System.Collections.ObjectModel;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Sources;

/// <summary>
/// Compares saved and newly inspected source schemas using the exact source-field
/// identity used by field mapping.
/// </summary>
public sealed class SourceSchemaComparisonService
{
    public SourceSchemaComparisonResult Compare(
        IReadOnlyList<SourceFieldDefinition> savedSchema,
        IReadOnlyList<SourceFieldDefinition> inspectedSchema,
        IReadOnlyList<FieldMapping> persistedMappings)
    {
        ArgumentNullException.ThrowIfNull(savedSchema);
        ArgumentNullException.ThrowIfNull(inspectedSchema);
        ArgumentNullException.ThrowIfNull(persistedMappings);

        var inspectedByName = inspectedSchema
            .Where(field => field is not null)
            .ToDictionary(field => field.Name, StringComparer.Ordinal);
        var savedNames = new HashSet<string>(
            savedSchema.Where(field => field is not null).Select(field => field.Name),
            StringComparer.Ordinal);

        var missingFields = savedSchema
            .Where(field => field is not null && !inspectedByName.ContainsKey(field.Name))
            .Select(CopyField)
            .ToArray();
        var newFields = inspectedSchema
            .Where(field => field is not null && !savedNames.Contains(field.Name))
            .Select(CopyField)
            .ToArray();
        var typeChanges = savedSchema
            .Where(field => field is not null
                && inspectedByName.TryGetValue(field.Name, out var inspected)
                && field.DataType != inspected.DataType)
            .Select(field => new SourceFieldTypeChange(
                field.Name,
                field.DataType,
                inspectedByName[field.Name].DataType))
            .ToArray();
        var unresolvedMappings = persistedMappings
            .Where(mapping => mapping is not null
                && !inspectedByName.ContainsKey(mapping.SourceField))
            .Select(CopyMapping)
            .ToArray();

        return new SourceSchemaComparisonResult(
            missingFields,
            newFields,
            typeChanges,
            unresolvedMappings);
    }

    private static SourceFieldDefinition CopyField(SourceFieldDefinition field) => new()
    {
        Name = field.Name,
        DataType = field.DataType
    };

    private static FieldMapping CopyMapping(FieldMapping mapping) => new()
    {
        SourceField = mapping.SourceField,
        TargetField = mapping.TargetField,
        IsIncluded = mapping.IsIncluded
    };
}

public sealed class SourceSchemaComparisonResult
{
    internal SourceSchemaComparisonResult(
        IReadOnlyList<SourceFieldDefinition> missingFields,
        IReadOnlyList<SourceFieldDefinition> newFields,
        IReadOnlyList<SourceFieldTypeChange> typeChanges,
        IReadOnlyList<FieldMapping> unresolvedMappings)
    {
        MissingFields = new ReadOnlyCollection<SourceFieldDefinition>(missingFields.ToArray());
        NewFields = new ReadOnlyCollection<SourceFieldDefinition>(newFields.ToArray());
        TypeChanges = new ReadOnlyCollection<SourceFieldTypeChange>(typeChanges.ToArray());
        UnresolvedMappings = new ReadOnlyCollection<FieldMapping>(unresolvedMappings.ToArray());
    }

    public IReadOnlyList<SourceFieldDefinition> MissingFields { get; }

    public IReadOnlyList<SourceFieldDefinition> NewFields { get; }

    public IReadOnlyList<SourceFieldTypeChange> TypeChanges { get; }

    public IReadOnlyList<FieldMapping> UnresolvedMappings { get; }

    public bool HasDifferences => MissingFields.Count > 0
        || NewFields.Count > 0
        || TypeChanges.Count > 0;

    public bool HasUnresolvedMappings => UnresolvedMappings.Count > 0;
}

public sealed record SourceFieldTypeChange(
    string FieldName,
    EtlTool.Domain.Enums.SourceFieldType SavedType,
    EtlTool.Domain.Enums.SourceFieldType InspectedType);
