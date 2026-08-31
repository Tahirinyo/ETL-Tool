using System.Runtime.CompilerServices;
using System.Text;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using static EtlTool.IntegrationTests.Extraction.OpenXmlWorkbookFixture;

namespace EtlTool.IntegrationTests.Extraction;

public sealed class FileEtlSourceTests
{
    [Fact]
    public async Task ReadAsync_IsDeferredAndForwardsCopiedOptionsAndCancellationToExtractor()
    {
        var stream = new TrackingMemoryStream([1]);
        var extractor = new RecordingExtractor();
        var options = new SourceOptions
        {
            CultureName = "tr-TR",
            DateFormat = "dd.MM.yyyy",
            Delimiter = CsvDelimiter.Semicolon,
            WorksheetName = "Data",
            FirstRowIsHeader = false
        };
        await using var source = new FileEtlSource(stream, extractor, options);
        using var cancellation = new CancellationTokenSource();

        var rows = source.ReadAsync(cancellation.Token);
        options.CultureName = "en-US";
        options.DateFormat = "MM/dd/yyyy";
        options.Delimiter = CsvDelimiter.Comma;
        options.WorksheetName = "Changed";
        options.FirstRowIsHeader = true;

        Assert.Equal(0, extractor.InvocationCount);
        Assert.Equal(0, stream.BytesRead);

        await using var enumerator = rows.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        Assert.Equal(1, extractor.InvocationCount);
        Assert.Equal(cancellation.Token, extractor.CancellationToken);
        Assert.NotSame(options, extractor.Options);
        Assert.Equal("tr-TR", extractor.Options!.CultureName);
        Assert.Equal("dd.MM.yyyy", extractor.Options.DateFormat);
        Assert.Equal(CsvDelimiter.Semicolon, extractor.Options.Delimiter);
        Assert.Equal("Data", extractor.Options.WorksheetName);
        Assert.False(extractor.Options.FirstRowIsHeader);
    }

    [Fact]
    public async Task DisposeAsync_OwnsUnderlyingStreamAndIsIdempotent()
    {
        var stream = new TrackingMemoryStream([1]);
        var source = new FileEtlSource(
            stream,
            new RecordingExtractor(),
            new SourceOptions());

        await source.DisposeAsync();
        await source.DisposeAsync();

        Assert.False(stream.CanRead);
        Assert.Equal(1, stream.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            ReadAllAsync(source.ReadAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_ForwardsCanceledTokenToExtractor()
    {
        var extractor = new RecordingExtractor();
        await using var source = new FileEtlSource(
            new MemoryStream([1]),
            extractor,
            new SourceOptions());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadAllAsync(source.ReadAsync(cancellation.Token)));

        Assert.Equal(1, extractor.InvocationCount);
        Assert.Equal(cancellation.Token, extractor.CancellationToken);
    }

    [Fact]
    public async Task ReadAsync_UsesExistingCsvExtractorWithConfiguredDelimiterAndRowNumbers()
    {
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes("Id;Name\n1;Ada"));
        await using var source = new FileEtlSource(
            stream,
            new CsvFileExtractor(),
            new SourceOptions { Delimiter = CsvDelimiter.Semicolon, FirstRowIsHeader = true });

        var row = Assert.Single(await ReadAllAsync(source.ReadAsync(CancellationToken.None)));

        Assert.Equal(2, row.SourceRowNumber);
        Assert.Equal("1", row.Values["Id"]);
        Assert.Equal("Ada", row.Values["Name"]);
    }

    [Fact]
    public async Task ReadAsync_UsesExistingXlsxExtractorWithConfiguredWorksheet()
    {
        await using var workbook = Create(
            Sheet("Ignored", Row(Text(1, "Id")), Row(Text(1, "ignored"))),
            Sheet("Data", Row(Text(1, "Id")), Row(Text(1, "selected"))));
        await using var source = new FileEtlSource(
            workbook,
            new XlsxFileExtractor(),
            new SourceOptions { WorksheetName = "Data", FirstRowIsHeader = true });

        var row = Assert.Single(await ReadAllAsync(source.ReadAsync(CancellationToken.None)));

        Assert.Equal(2, row.SourceRowNumber);
        Assert.Equal("selected", row.Values["Id"]);
    }

    private static async Task<IReadOnlyList<DataRow>> ReadAllAsync(IAsyncEnumerable<DataRow> rows)
    {
        var result = new List<DataRow>();
        await foreach (var row in rows)
        {
            result.Add(row);
        }

        return result;
    }

    private sealed class RecordingExtractor : IFileExtractor
    {
        public SourceType SourceType => SourceType.Csv;

        public int InvocationCount { get; private set; }

        public SourceOptions? Options { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<string>> ReadHeadersAsync(
            Stream stream,
            SourceOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<DataRow> ReadAsync(
            Stream stream,
            SourceOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            InvocationCount++;
            Options = options;
            CancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            _ = await stream.ReadAsync(new byte[1], cancellationToken);
            var row = new DataRow { SourceRowNumber = 2 };
            row.Values["Id"] = "1";
            yield return row;
        }
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public long BytesRead { get; private set; }

        public int DisposeCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}
