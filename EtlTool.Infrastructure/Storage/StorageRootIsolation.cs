namespace EtlTool.Infrastructure.Storage;

public static class StorageRootIsolation
{
    public static void EnsureSeparate(string uploadRootPath, string errorReportRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorReportRootPath);

        var uploadRoot = Path.GetFullPath(uploadRootPath);
        var errorReportRoot = Path.GetFullPath(errorReportRootPath);

        if (IsSameOrDescendant(uploadRoot, errorReportRoot)
            || IsSameOrDescendant(errorReportRoot, uploadRoot))
        {
            throw new InvalidOperationException(
                "Upload storage and error-report storage must use separate, non-overlapping roots.");
        }
    }

    private static bool IsSameOrDescendant(string parentPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(parentPath, candidatePath);

        return relative == "."
            || (!Path.IsPathFullyQualified(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
