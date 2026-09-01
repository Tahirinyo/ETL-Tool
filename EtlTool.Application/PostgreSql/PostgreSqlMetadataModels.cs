namespace EtlTool.Application.PostgreSql;

public sealed record PostgreSqlDatabaseMetadata(string Name);

public sealed record PostgreSqlSchemaMetadata(string Name);

// Discovery is intentionally limited to PostgreSQL base tables in this MVP.
public sealed record PostgreSqlTableMetadata(string Name);

public sealed record PostgreSqlColumnMetadata(
    string Name,
    string NativeType,
    bool IsNullable,
    int OrdinalPosition);

public enum PostgreSqlKeyConstraintKind
{
    PrimaryKey = 1,
    Unique = 2
}

public sealed record PostgreSqlKeyColumnMetadata(
    string Name,
    int KeyOrdinal,
    bool IsNullable);

public sealed record PostgreSqlKeyConstraintMetadata(
    string Name,
    PostgreSqlKeyConstraintKind Kind,
    IReadOnlyList<PostgreSqlKeyColumnMetadata> Columns,
    bool IsNullsNotDistinct = false,
    bool IsDeferrable = false);
