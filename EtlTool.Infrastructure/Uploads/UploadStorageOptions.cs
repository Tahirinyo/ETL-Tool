namespace EtlTool.Infrastructure.Uploads;

public sealed class UploadStorageOptions
{
    public const string SectionName = "UploadStorage";

    public string RootPath { get; set; } = "App_Data/uploads";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:RootPath' is required.");
        }

        if (!Path.IsPathFullyQualified(RootPath))
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:RootPath' must resolve to an absolute path.");
        }
    }

    public string ResolveRootPath(string contentRootPath, string? webRootPath)
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:RootPath' is required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var resolvedContentRoot = Path.GetFullPath(contentRootPath);
        var resolvedRoot = Path.GetFullPath(RootPath, resolvedContentRoot);
        var resolvedWebRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(webRootPath)
                ? Path.Combine(resolvedContentRoot, "wwwroot")
                : webRootPath,
            resolvedContentRoot);

        if (IsSameOrDescendant(resolvedWebRoot, resolvedRoot))
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:RootPath' must be outside the web root.");
        }

        return resolvedRoot;
    }

    private static bool IsSameOrDescendant(string parentPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(parentPath, candidatePath);

        return relativePath == "."
            || (!Path.IsPathFullyQualified(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
