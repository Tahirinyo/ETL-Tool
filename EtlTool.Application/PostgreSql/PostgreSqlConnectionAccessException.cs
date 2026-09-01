namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlConnectionAccessException : Exception
{
    public PostgreSqlConnectionAccessException(bool isTransient = false)
        : base("The configured PostgreSQL source could not be accessed.")
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}
