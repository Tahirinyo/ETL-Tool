using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;

namespace EtlTool.UnitTests.Infrastructure.Extraction;

public sealed class FileExtractorResolverTests
{
    [Fact]
    public void Resolve_ReturnsRegisteredCsvAndXlsxExtractors()
    {
        var csv = new CsvFileExtractor();
        var xlsx = new XlsxFileExtractor();
        var resolver = new FileExtractorResolver([csv, xlsx]);

        Assert.Same(csv, resolver.Resolve(SourceType.Csv));
        Assert.Same(xlsx, resolver.Resolve(SourceType.Xlsx));
    }

    [Fact]
    public void Constructor_RejectsDuplicateInvalidAndUnsupportedRegistrations()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FileExtractorResolver([new CsvFileExtractor(), new CsvFileExtractor()]));
        Assert.Throws<ArgumentException>(() =>
            new FileExtractorResolver(new IFileExtractor[] { null! }));
        Assert.Throws<InvalidOperationException>(() =>
            new FileExtractorResolver([new StubExtractor(SourceType.Unspecified)]));
        Assert.Throws<ArgumentNullException>(() =>
            new FileExtractorResolver(null!));
    }

    [Fact]
    public void Resolve_RejectsMissingAndUnsupportedSourceTypes()
    {
        var resolver = new FileExtractorResolver([new CsvFileExtractor()]);

        Assert.Throws<KeyNotFoundException>(() => resolver.Resolve(SourceType.Xlsx));
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Resolve(SourceType.Unspecified));
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Resolve((SourceType)999));
    }

    private sealed class StubExtractor(SourceType sourceType) : IFileExtractor
    {
        public SourceType SourceType { get; } = sourceType;

        public Task<IReadOnlyList<string>> ReadHeadersAsync(
            Stream stream,
            SourceOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public async IAsyncEnumerable<DataRow> ReadAsync(
            Stream stream,
            SourceOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
