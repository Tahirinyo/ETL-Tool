using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Mapping;

public sealed class FieldMappingService
{
    public FieldMappingPlan Prepare(PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        ValidateExpectedSchema(pipeline.ExpectedSchema);
        var schemaFields = pipeline.ExpectedSchema
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (pipeline.FieldMappings is null)
        {
            throw new InvalidOperationException(
                "The field mapping collection is missing.");
        }

        var activeMappings = new List<FieldMappingPlan.MappingEntry>(pipeline.FieldMappings.Count);
        var sourceFields = new HashSet<string>(StringComparer.Ordinal);
        var targetFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in pipeline.FieldMappings)
        {
            if (mapping is null)
            {
                throw new InvalidOperationException(
                    "The field mapping collection contains an invalid mapping.");
            }

            if (!mapping.IsIncluded)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.SourceField))
            {
                throw new InvalidOperationException(
                    "An active field mapping has an empty source field name.");
            }

            if (string.IsNullOrWhiteSpace(mapping.TargetField))
            {
                throw new InvalidOperationException(
                    $"The active mapping for source field '{mapping.SourceField}' has an empty target field name.");
            }

            if (!schemaFields.Contains(mapping.SourceField))
            {
                throw new InvalidOperationException(
                    $"Mapped source field '{mapping.SourceField}' does not exist in the expected source schema.");
            }

            if (!sourceFields.Add(mapping.SourceField))
            {
                throw new InvalidOperationException(
                    $"Source field '{mapping.SourceField}' has more than one active mapping.");
            }

            if (!targetFields.Add(mapping.TargetField))
            {
                throw new InvalidOperationException(
                    $"Target field '{mapping.TargetField}' is used by more than one active mapping.");
            }

            activeMappings.Add(new FieldMappingPlan.MappingEntry(
                mapping.SourceField,
                mapping.TargetField));
        }

        if (activeMappings.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one active field mapping is required.");
        }

        return new FieldMappingPlan(activeMappings.ToArray());
    }

    internal static void ValidateExpectedSchema(IReadOnlyList<SourceFieldDefinition>? expectedSchema)
    {
        if (expectedSchema is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Field mapping requires a non-empty expected source schema.");
        }

        var schemaFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in expectedSchema)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Name))
            {
                throw new InvalidOperationException(
                    "The expected source schema contains an empty field name.");
            }

            if (!schemaFields.Add(field.Name))
            {
                throw new InvalidOperationException(
                    $"The expected source schema contains duplicate field '{field.Name}'.");
            }
        }
    }

    public DataRow Apply(DataRow sourceRow, FieldMappingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(sourceRow);
        ArgumentNullException.ThrowIfNull(plan);

        var mappedRow = new DataRow
        {
            SourceRowNumber = sourceRow.SourceRowNumber
        };

        foreach (var mapping in plan.Mappings)
        {
            if (!sourceRow.Values.TryGetValue(mapping.SourceField, out var value))
            {
                throw new InvalidOperationException(
                    $"Mapped source field '{mapping.SourceField}' is missing from source row {sourceRow.SourceRowNumber}.");
            }

            mappedRow.Values.Add(mapping.TargetField, value);
        }

        return mappedRow;
    }
}
