namespace EtlTool.Domain.ValueObjects;

public sealed class PostgreSqlSourceOptions
{
    public Guid? SavedConnectionId { get; set; }

    public int? SavedConnectionRevision { get; set; }

    public string ConnectionProfile { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string Schema { get; set; } = string.Empty;

    public string Table { get; set; } = string.Empty;
}
