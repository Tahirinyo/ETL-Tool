namespace EtlTool.Application.MongoDB;

public sealed class MongoTargetAccessException : Exception
{
    public MongoTargetAccessException()
        : base("The configured MongoDB destination could not be accessed.")
    {
    }
}
