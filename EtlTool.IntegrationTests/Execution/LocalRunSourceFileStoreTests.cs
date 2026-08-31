using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
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
        await File.WriteAllTextAsync(path, "Id\nsource");
        var run = Run(path);
        var store = CreateStore();

        await using (var source = await store.OpenAsync(run, CancellationToken.None))
        {
            var rows = new List<DataRow>();
            await foreach (var row in source.ReadAsync(CancellationToken.None))
            {
                rows.Add(row);
            }
            Assert.Equal("source", Assert.Single(rows).Values["Id"]);
        }
        await store.ReleaseAsync(run, CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("../outside.upload")]
    [InlineData("not-generated.upload")]
    [InlineData("source.csv")]
    public async Task Open_RejectsUntrustedPersistedPath(string fileName)
    {
        var store = CreateStore();
        var run = Run(Path.Combine(_rootPath, fileName));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.OpenAsync(run, CancellationToken.None));
    }

    [Fact]
    public async Task OpenAndDeleteAsync_RejectsUppercaseGuidShapedFileThatUploadStorageDoesNotOwn()
    {
        Directory.CreateDirectory(_rootPath);
        var fileName = $"{Guid.NewGuid():N}".ToUpperInvariant() + ".upload";
        var path = Path.Combine(_rootPath, fileName);
        await File.WriteAllTextAsync(path, "unrelated");
        var run = Run(path);
        var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.OpenAsync(run, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReleaseAsync(run, CancellationToken.None));
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
        return new LocalRunSourceFileStore(
            options,
            new LocalUploadStorage(options),
            new FileExtractorResolver([new CsvFileExtractor(), new XlsxFileExtractor()]));
    }

    private static EtlRun Run(string path) => new()
    {
        Id = Guid.NewGuid(),
        StoredFilePath = path,
        ExecutionConfiguration = new EtlRunExecutionConfiguration
        {
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions
            {
                Delimiter = CsvDelimiter.Comma,
                FirstRowIsHeader = true
            }
        }
    };
}
