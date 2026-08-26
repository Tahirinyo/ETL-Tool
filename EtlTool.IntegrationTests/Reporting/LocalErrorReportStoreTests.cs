using EtlTool.Domain.Entities;
using EtlTool.Infrastructure.Reporting;

namespace EtlTool.IntegrationTests.Reporting;

public sealed class LocalErrorReportStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"EtlTool-ErrorReports-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishAsync_PublishesOnlyRunOwnedGeneratedReference()
    {
        var run = new EtlRun { Id = Guid.NewGuid() };
        var store = CreateStore();
        await using var output = store.CreateOutput(run);

        await output.Stream.WriteAsync("csv"u8.ToArray());
        var reference = await output.PublishAsync(CancellationToken.None);

        Assert.Equal($"error-report-{run.Id:N}.csv", reference);
        run.ErrorReportPath = reference;
        await using var stream = Assert.IsType<FileStream>(store.OpenRead(run));
        Assert.Equal(3, stream.Length);
        Assert.DoesNotContain(_rootPath, reference, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.csv")]
    [InlineData("..\\outside.csv")]
    [InlineData("C:\\outside.csv")]
    [InlineData("nested/report.csv")]
    [InlineData("nested\\report.csv")]
    [InlineData("report.csv")]
    public void OpenRead_RejectsMalformedPersistedReference(string reference)
    {
        var store = CreateStore();
        var run = new EtlRun { Id = Guid.NewGuid(), ErrorReportPath = reference };

        Assert.Null(store.OpenRead(run));
    }

    [Fact]
    public async Task OpenRead_RejectsAnotherRunsCopiedReference()
    {
        var store = CreateStore();
        var owner = new EtlRun { Id = Guid.NewGuid() };
        await using (var output = store.CreateOutput(owner))
        {
            await output.Stream.WriteAsync("csv"u8.ToArray());
            owner.ErrorReportPath = await output.PublishAsync(CancellationToken.None);
        }

        var attacker = new EtlRun { Id = Guid.NewGuid(), ErrorReportPath = owner.ErrorReportPath };

        Assert.Null(store.OpenRead(attacker));
        await using var ownerStream = Assert.IsType<FileStream>(store.OpenRead(owner));
    }

    [Fact]
    public async Task AbortAsync_RemovesUnpublishedTemporaryOutput()
    {
        var store = CreateStore();
        await using var output = store.CreateOutput(new EtlRun { Id = Guid.NewGuid() });
        await output.Stream.WriteAsync("partial"u8.ToArray());

        await output.AbortAsync();

        Assert.Empty(Directory.EnumerateFiles(_rootPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private LocalErrorReportStore CreateStore() => new(new ErrorReportStorageOptions { RootPath = _rootPath });
}
