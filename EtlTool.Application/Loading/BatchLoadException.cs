namespace EtlTool.Application.Loading;

public sealed class BatchLoadException : Exception
{
    public BatchLoadException(
        string message,
        BatchLoadResult confirmedResult,
        Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(confirmedResult);
        ArgumentNullException.ThrowIfNull(innerException);

        ConfirmedResult = confirmedResult;
    }

    public BatchLoadResult ConfirmedResult { get; }
}
