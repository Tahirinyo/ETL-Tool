namespace EtlTool.Application.MongoDB;

public sealed class MongoSourceMetadataObjectNotFoundException : Exception
{
    public MongoSourceMetadataObjectNotFoundException(string objectType)
        : base($"The selected MongoDB {objectType} was not found or is not accessible.")
    {
        ObjectType = objectType;
    }

    public string ObjectType { get; }
}
