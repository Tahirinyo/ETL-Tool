namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoDbOptions
{
    public const string SectionName = "MongoDb";

    public string ConnectionString { get; set; } = string.Empty;

    public string MetadataDatabaseName { get; set; } = "etl_tool_metadata";

    public int BulkWriteMaximumAttempts { get; set; } = 2;

    public int BulkWriteRetryDelayMilliseconds { get; set; } = 200;

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

        if (BulkWriteMaximumAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:BulkWriteMaximumAttempts' must be between 1 and 10.");
        }

        if (BulkWriteRetryDelayMilliseconds is < 0 or > 60_000)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:BulkWriteRetryDelayMilliseconds' must be between 0 and 60000.");
        }
    }
}
