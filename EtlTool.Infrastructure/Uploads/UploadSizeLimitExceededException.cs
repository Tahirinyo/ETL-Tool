namespace EtlTool.Infrastructure.Uploads;

internal sealed class UploadSizeLimitExceededException(long maximumBytes)
    : IOException($"The uploaded content exceeds the configured limit of {maximumBytes} bytes.")
{
}
