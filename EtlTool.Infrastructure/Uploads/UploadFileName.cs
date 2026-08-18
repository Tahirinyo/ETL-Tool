namespace EtlTool.Infrastructure.Uploads;

internal static class UploadFileName
{
    public static string GetLeafName(string originalFileName)
    {
        ArgumentNullException.ThrowIfNull(originalFileName);

        var normalizedSeparators = originalFileName.Replace('\\', '/');
        var lastSeparator = normalizedSeparators.LastIndexOf('/');
        var leafFileName = normalizedSeparators[(lastSeparator + 1)..];

        if (string.IsNullOrWhiteSpace(leafFileName)
            || leafFileName is "." or "..")
        {
            throw new ArgumentException(
                "The original filename must contain a non-empty leaf filename.",
                nameof(originalFileName));
        }

        return leafFileName;
    }
}
