using EtlTool.Domain.Entities;
using EtlTool.Infrastructure.Execution;
using EtlTool.Infrastructure.Uploads;

namespace EtlTool.IntegrationTests.Execution;

public sealed class LocalRunSourceFileStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"EtlTool-RunSources-{Guid.NewGuid():N}");

    [Fact]
    public async Task OpenAndDeleteAsync_AcceptsOnlyGeneratedUploadInsideRoot()
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, $"{Guid.NewGuid():N}.upload");
        await File.WriteAllTextAsync(path, "source");
        var run = new EtlRun { Id = Guid.NewGuid(), StoredFilePath = path };
        var store = CreateStore();

        await using (var stream = store.Open(run))
        {
            Assert.True(stream.CanRead);
        }
        await store.DeleteAsync(run, CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("../outside.upload")]
    [InlineData("not-generated.upload")]
    [InlineData("source.csv")]
    public void Open_RejectsUntrustedPersistedPath(string fileName)
    {
        var store = CreateStore();
        var run = new EtlRun { Id = Guid.NewGuid(), StoredFilePath = Path.Combine(_rootPath, fileName) };

        Assert.Throws<InvalidOperationException>(() => store.Open(run));
    }

    [Fact]
    public async Task OpenAndDeleteAsync_RejectsUppercaseGuidShapedFileThatUploadStorageDoesNotOwn()
    {
        Directory.CreateDirectory(_rootPath);
        var fileName = $"{Guid.NewGuid():N}".ToUpperInvariant() + ".upload";
        var path = Path.Combine(_rootPath, fileName);
        await File.WriteAllTextAsync(path, "unrelated");
        var run = new EtlRun { Id = Guid.NewGuid(), StoredFilePath = path };
        var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() => store.Open(run));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteAsync(run, CancellationToken.None));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private LocalRunSourceFileStore CreateStore()
    {
        var options = new UploadStorageOptions { RootPath = _rootPath };
        return new LocalRunSourceFileStore(options, new LocalUploadStorage(options));
    }
}
