namespace EtlTool.Domain.ValueObjects;

// MongoDB uses the application's single configured MongoDb connection.
// Only the selected source namespace is persisted with a pipeline.
public sealed class MongoDbSourceOptions
{
    public Guid? SavedConnectionId { get; set; }

    public int? SavedConnectionRevision { get; set; }

    public string Database { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;
}
