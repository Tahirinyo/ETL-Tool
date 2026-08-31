namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlMetadataObjectNotFoundException : Exception
{
    public PostgreSqlMetadataObjectNotFoundException(string objectType)
        : base($"The selected PostgreSQL {objectType} was not found or is not accessible.")
    {
        ObjectType = objectType;
    }

    public string ObjectType { get; }
}
