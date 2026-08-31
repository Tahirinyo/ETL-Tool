namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlConnectionProfileNotFoundException : Exception
{
    public PostgreSqlConnectionProfileNotFoundException()
        : base("The configured PostgreSQL connection profile was not found.")
    {
    }
}
