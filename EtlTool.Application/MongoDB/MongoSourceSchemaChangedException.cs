namespace EtlTool.Application.MongoDB;

public sealed class MongoSourceSchemaChangedException : InvalidOperationException
{
    public const string SafeMessage =
        "The MongoDB source schema has changed. Refresh the source schema and remap the pipeline before running it again.";

    public MongoSourceSchemaChangedException()
        : base(SafeMessage)
    {
    }

    public MongoSourceSchemaChangedException(Exception innerException)
        : base(SafeMessage, innerException)
    {
    }
}
