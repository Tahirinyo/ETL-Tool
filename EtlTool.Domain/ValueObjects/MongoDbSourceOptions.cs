namespace EtlTool.Domain.ValueObjects;

// MongoDB uses the application's single configured MongoDb connection.
// Only the selected source namespace is persisted with a pipeline.
public sealed class MongoDbSourceOptions
{
    public string Database { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;
}
