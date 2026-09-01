namespace EtlTool.Domain.ValueObjects;

/// <summary>
/// Logical PostgreSQL destination identity and the explicit output-to-column projection.
/// Connection material remains in the configured profile, never in a pipeline.
/// </summary>
public sealed class PostgreSqlDestinationOptions
{
    public Guid? SavedConnectionId { get; set; }

    public int? SavedConnectionRevision { get; set; }

    public string ConnectionProfile { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string Schema { get; set; } = string.Empty;

    public string Table { get; set; } = string.Empty;

    public List<PostgreSqlDestinationColumnMapping> ColumnMappings { get; set; } = [];

    public string UpsertKeyColumn { get; set; } = string.Empty;
}

public sealed class PostgreSqlDestinationColumnMapping
{
    public string OutputField { get; set; } = string.Empty;

    public string DestinationColumn { get; set; } = string.Empty;
}
