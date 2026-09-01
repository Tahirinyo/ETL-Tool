namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlDestinationPreparationException : Exception
{
    public const string SafeMessage = "The PostgreSQL destination could not be prepared for execution.";

    public PostgreSqlDestinationPreparationException(Exception innerException)
        : base(SafeMessage, innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }
}
