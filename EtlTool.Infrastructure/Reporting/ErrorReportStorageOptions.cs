namespace EtlTool.Infrastructure.Reporting;

public sealed class ErrorReportStorageOptions
{
    public const string SectionName = "ErrorReportStorage";

    public string RootPath { get; set; } = "App_Data/error-reports";

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
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var contentRoot = Path.GetFullPath(contentRootPath);
        var root = Path.GetFullPath(RootPath, contentRoot);
        var webRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(webRootPath) ? Path.Combine(contentRoot, "wwwroot") : webRootPath,
            contentRoot);
        var relative = Path.GetRelativePath(webRoot, root);
        if (relative == "." || (!Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:RootPath' must be outside the web root.");
        }

        return root;
    }
}
