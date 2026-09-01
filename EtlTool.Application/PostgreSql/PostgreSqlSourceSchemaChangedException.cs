namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlSourceSchemaChangedException : InvalidOperationException
{
    public const string SafeMessage =
        "The PostgreSQL source schema has changed. Refresh the source schema and remap the pipeline before running it again.";

    public PostgreSqlSourceSchemaChangedException()
        : base(SafeMessage)
    {
    }

    public PostgreSqlSourceSchemaChangedException(Exception innerException)
        : base(SafeMessage, innerException)
    {
    }
}
