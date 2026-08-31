namespace EtlTool.Application.PostgreSql;

public sealed record PostgreSqlSourceOrdering(
    string ConstraintName,
    IReadOnlyList<string> Columns);
