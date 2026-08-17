namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoDbOptions
{
    public const string SectionName = "MongoDb";

    public string ConnectionString { get; set; } = string.Empty;

    public string MetadataDatabaseName { get; set; } = "etl_tool_metadata";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"MongoDB connection string is required. Configure '{SectionName}__ConnectionString' " +
                "through an environment variable, user secrets, or another secret configuration provider.");
        }

        if (string.IsNullOrWhiteSpace(MetadataDatabaseName))
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:MetadataDatabaseName' is required.");
        }
    }
}
