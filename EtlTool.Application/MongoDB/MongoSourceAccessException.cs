namespace EtlTool.Application.MongoDB;

public sealed class MongoSourceAccessException : Exception
{
    public MongoSourceAccessException()
        : base("The configured MongoDB source could not be accessed.")
    {
    }
}
