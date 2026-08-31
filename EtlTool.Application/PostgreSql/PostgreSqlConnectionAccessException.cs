namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlConnectionAccessException : Exception
{
    public PostgreSqlConnectionAccessException()
        : base("The configured PostgreSQL source could not be accessed.")
    {
    }
}
