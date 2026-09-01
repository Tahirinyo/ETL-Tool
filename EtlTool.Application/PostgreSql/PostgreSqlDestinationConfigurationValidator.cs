using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.PostgreSql;

public static class PostgreSqlDestinationConfigurationValidator
{
    public static void Validate(
        PostgreSqlDestinationOptions? destination,
        ISet<string> activeOutputFields,
        IReadOnlyList<PostgreSqlColumnMetadata>? columns = null,
        IReadOnlyList<PostgreSqlKeyConstraintMetadata>? keyConstraints = null)
    {
        ArgumentNullException.ThrowIfNull(activeOutputFields);

        if (destination is null)
        {
            throw new InvalidOperationException("The PostgreSQL destination configuration is missing.");
        }

        if (!destination.SavedConnectionId.HasValue)
        {
            Require(destination.ConnectionProfile, "connection profile");
        }
        else if (destination.SavedConnectionId == Guid.Empty)
        {
            throw new InvalidOperationException("The PostgreSQL saved connection identifier is invalid.");
        }
        Require(destination.Database, "database");
        Require(destination.Schema, "schema");
        Require(destination.Table, "table");
        Require(destination.UpsertKeyColumn, "upsert-key column");

        var mappings = destination.ColumnMappings
            ?? throw new InvalidOperationException("The PostgreSQL destination column mappings are missing.");
        var outputFields = new HashSet<string>(StringComparer.Ordinal);
        var destinationColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (mapping is null)
            {
                throw new InvalidOperationException("The PostgreSQL destination column mappings contain an invalid entry.");
            }

            Require(mapping.OutputField, "mapped output field");
            Require(mapping.DestinationColumn, "mapped destination column");
            if (!activeOutputFields.Contains(mapping.OutputField))
            {
                throw new InvalidOperationException(
                    $"The PostgreSQL mapped output field '{mapping.OutputField}' is not an included mapped output field.");
            }

            if (!outputFields.Add(mapping.OutputField))
            {
                throw new InvalidOperationException(
                    $"The PostgreSQL output field '{mapping.OutputField}' is mapped more than once.");
            }

            if (!destinationColumns.Add(mapping.DestinationColumn))
            {
                throw new InvalidOperationException(
                    $"The PostgreSQL destination column '{mapping.DestinationColumn}' is mapped more than once.");
            }
        }

        if (!destinationColumns.Contains(destination.UpsertKeyColumn))
        {
            throw new InvalidOperationException(
                "The PostgreSQL upsert-key column must be mapped from an included output field.");
        }

        if (columns is not null)
        {
            var knownColumns = columns
                .Where(column => column is not null && !string.IsNullOrWhiteSpace(column.Name))
                .Select(column => column.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (mappings.Any(mapping => !knownColumns.Contains(mapping.DestinationColumn)))
            {
                throw new InvalidOperationException(
                    "A PostgreSQL destination column mapping no longer exists on the selected table.");
            }
        }

        if (keyConstraints is not null && !IsEligibleSingleColumnKey(destination.UpsertKeyColumn, keyConstraints))
        {
            throw new InvalidOperationException(
                "The PostgreSQL upsert-key column must be backed by a non-null single-column PRIMARY KEY or UNIQUE constraint/index.");
        }
    }

    public static IReadOnlyList<string> GetEligibleSingleColumnKeyColumns(
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> keyConstraints) => keyConstraints
        .Where(constraint => constraint is not null
            && constraint.Kind is PostgreSqlKeyConstraintKind.PrimaryKey or PostgreSqlKeyConstraintKind.Unique
            && constraint.Columns is { Count: 1 }
            && constraint.Columns[0] is { IsNullable: false, KeyOrdinal: 1 }
            && !string.IsNullOrWhiteSpace(constraint.Columns[0].Name)
            && !constraint.IsDeferrable
            && (constraint.Kind != PostgreSqlKeyConstraintKind.Unique || !constraint.IsNullsNotDistinct))
        .Select(constraint => constraint.Columns[0].Name)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    private static bool IsEligibleSingleColumnKey(
        string column,
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> keyConstraints) =>
        GetEligibleSingleColumnKeyColumns(keyConstraints).Contains(column, StringComparer.Ordinal);

    private static void Require(string? value, string displayName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"The PostgreSQL destination {displayName} is required.");
        }
    }
}
