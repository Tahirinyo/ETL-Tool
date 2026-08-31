namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlDeterministicOrderingResolver
{
    public PostgreSqlSourceOrdering Resolve(
        IReadOnlyList<PostgreSqlKeyConstraintMetadata> constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        var candidates = constraints
            .Select(CreateCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();

        var selected = candidates
            .Where(candidate => candidate.Kind == PostgreSqlKeyConstraintKind.PrimaryKey)
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.OrderedColumnNames, OrderedColumnNamesComparer.Instance)
            .FirstOrDefault()
            ?? candidates
                .Where(candidate => candidate.Kind == PostgreSqlKeyConstraintKind.Unique)
                .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.OrderedColumnNames, OrderedColumnNamesComparer.Instance)
                .FirstOrDefault();

        return selected is null
            ? throw new PostgreSqlDeterministicOrderingUnavailableException()
            : new PostgreSqlSourceOrdering(selected.Name, selected.OrderedColumnNames);
    }

    private static Candidate? CreateCandidate(PostgreSqlKeyConstraintMetadata? constraint)
    {
        if (constraint is null
            || string.IsNullOrWhiteSpace(constraint.Name)
            || constraint.Columns is null
            || constraint.Columns.Count == 0
            || constraint.Kind is not PostgreSqlKeyConstraintKind.PrimaryKey
                and not PostgreSqlKeyConstraintKind.Unique)
        {
            return null;
        }

        var columns = constraint.Columns
            .OrderBy(column => column?.KeyOrdinal ?? int.MaxValue)
            .ToArray();

        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index];
            if (column is null
                || string.IsNullOrWhiteSpace(column.Name)
                || column.KeyOrdinal != index + 1)
            {
                return null;
            }
        }

        if (columns.Any(column => column.IsNullable))
        {
            return null;
        }

        if (constraint.Kind == PostgreSqlKeyConstraintKind.Unique
            && constraint.IsNullsNotDistinct)
        {
            return null;
        }

        return new Candidate(
            constraint.Name,
            constraint.Kind,
            columns.Select(column => column.Name).ToArray());
    }

    private sealed record Candidate(
        string Name,
        PostgreSqlKeyConstraintKind Kind,
        IReadOnlyList<string> OrderedColumnNames);

    private sealed class OrderedColumnNamesComparer : IComparer<IReadOnlyList<string>>
    {
        public static OrderedColumnNamesComparer Instance { get; } = new();

        public int Compare(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            for (var index = 0; index < Math.Min(x.Count, y.Count); index++)
            {
                var comparison = StringComparer.Ordinal.Compare(x[index], y[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return x.Count.CompareTo(y.Count);
        }
    }
}
