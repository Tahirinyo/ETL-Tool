using EtlTool.Infrastructure.Reporting;
using EtlTool.Infrastructure.Storage;
using EtlTool.Infrastructure.Uploads;

namespace EtlTool.IntegrationTests.Uploads;

public sealed class StorageRootIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"EtlTool-StorageRootIsolation-{Guid.NewGuid():N}");

    [Fact]
    public void EnsureSeparate_RejectsEquivalentCanonicalRoots()
    {
        var root = Path.Combine(_root, "files");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StorageRootIsolation.EnsureSeparate(
                Path.Combine(root, "."),
                Path.Combine(root, "nested", "..")));

        Assert.Contains("separate", exception.Message);
    }

    [Theory]
    [InlineData("uploads", "uploads/reports")]
    [InlineData("uploads", "uploads/reports/archive")]
    [InlineData("files", "files/../files")]
    [InlineData("uploads/reports", "uploads")]
    public void EnsureSeparate_RejectsEitherRootNestedUnderTheOther(
        string uploadRelativePath,
        string reportRelativePath)
    {
        Assert.Throws<InvalidOperationException>(() =>
            StorageRootIsolation.EnsureSeparate(
                Path.Combine(_root, uploadRelativePath),
                Path.Combine(_root, reportRelativePath)));
    }

    [Fact]
    public void EnsureSeparate_RejectsTrailingSeparatorVariant()
    {
        var root = Path.Combine(_root, "files");

        Assert.Throws<InvalidOperationException>(() =>
            StorageRootIsolation.EnsureSeparate(root + Path.DirectorySeparatorChar, root));
    }

    [Theory]
    [InlineData("uploads", "error-reports")]
    [InlineData("uploads", "uploads-old")]
    public void EnsureSeparate_AcceptsSeparateSiblingRoots(
        string uploadRelativePath,
        string reportRelativePath)
    {
        StorageRootIsolation.EnsureSeparate(
            Path.Combine(_root, uploadRelativePath),
            Path.Combine(_root, reportRelativePath));
    }

    [Fact]
    public void DefaultResolvedStorageRoots_AreSeparate()
    {
        var contentRoot = Path.Combine(_root, "content");
        var webRoot = Path.Combine(contentRoot, "wwwroot");
        var upload = new UploadStorageOptions().ResolveRootPath(contentRoot, webRoot);
        var reports = new ErrorReportStorageOptions().ResolveRootPath(contentRoot, webRoot);

        StorageRootIsolation.EnsureSeparate(upload, reports);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
