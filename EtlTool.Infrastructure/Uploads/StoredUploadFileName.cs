namespace EtlTool.Infrastructure.Uploads;

internal static class StoredUploadFileName
{
    public const string Extension = ".upload";

    public static bool TryParse(string? fileName, out string canonicalFileName)
    {
        canonicalFileName = string.Empty;
        if (string.IsNullOrEmpty(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !fileName.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        var identifierText = fileName[..^Extension.Length];
        if (!Guid.TryParseExact(identifierText, "N", out var identifier))
        {
            return false;
        }

        canonicalFileName = $"{identifier:N}{Extension}";
        return string.Equals(fileName, canonicalFileName, StringComparison.Ordinal);
    }
}
