namespace EtlTool.Infrastructure.Connections;

public sealed class SavedConnectionProtectionOptions
{
    public const string SectionName = "SavedConnectionProtection";

    public string KeyRingPath { get; set; } = "App_Data/data-protection-keys";

    public string ResolveKeyRingPath(string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        return Path.GetFullPath(
            Path.IsPathRooted(KeyRingPath)
                ? KeyRingPath
                : Path.Combine(contentRootPath, KeyRingPath));
    }
}
