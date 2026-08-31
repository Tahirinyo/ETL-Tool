namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlDeterministicOrderingUnavailableException : InvalidOperationException
{
    public PostgreSqlDeterministicOrderingUnavailableException()
        : base("The configured PostgreSQL source does not have a deterministic primary-key or eligible unique-constraint ordering.")
    {
    }
}
