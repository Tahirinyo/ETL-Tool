namespace EtlTool.Application.PostgreSql;

public sealed class PostgreSqlUnsupportedColumnTypeException : InvalidOperationException
{
    public PostgreSqlUnsupportedColumnTypeException(string columnName, string nativeType)
        : base($"The PostgreSQL column '{columnName}' uses unsupported type '{nativeType}'.")
    {
        ColumnName = columnName;
        NativeType = nativeType;
    }

    public string ColumnName { get; }

    public string NativeType { get; }
}
