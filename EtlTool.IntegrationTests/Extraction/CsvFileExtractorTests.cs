using System.Text;
using CsvHelper;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;

namespace EtlTool.IntegrationTests.Extraction;

public sealed class CsvFileExtractorTests
{
    [Fact]
    public async Task ReadAsync_YieldsMappedRowsWithLogicalSourceNumbers()
    {
        using var stream = CreateStream("Name,Age\r\nAda,36\r\nGrace,85");
        IFileExtractor extractor = new CsvFileExtractor();

        var rows = await ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None));

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal(2, row.SourceRowNumber);
                Assert.Equal("Ada", Assert.IsType<string>(row.Values["Name"]));
                Assert.Equal("36", Assert.IsType<string>(row.Values["Age"]));
            },
            row =>
            {
                Assert.Equal(3, row.SourceRowNumber);
                Assert.Equal("Grace", Assert.IsType<string>(row.Values["Name"]));
                Assert.Equal("85", Assert.IsType<string>(row.Values["Age"]));
            });
    }

    [Fact]
    public async Task ReadAsync_UsesLogicalRecordNumbersForQuotedMultilineFields()
    {
        using var stream = CreateStream("Id,Notes\r\n1,\"first line\r\nsecond line\"\r\n2,plain");
        var extractor = new CsvFileExtractor();

        var rows = await ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None));

        Assert.Equal([2L, 3L], rows.Select(row => row.SourceRowNumber));
        Assert.Equal(
            "first line\r\nsecond line",
            Assert.IsType<string>(rows[0].Values["Notes"]));
    }

    [Fact]
    public async Task ReadAsync_ParsesQuotedDelimitersAndEscapedQuotes()
    {
        using var stream = CreateStream("Id,Description\n1,\"Doe, Jane said \"\"hello\"\"\"");
        var extractor = new CsvFileExtractor();

        var rows = await ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal("Doe, Jane said \"hello\"", Assert.IsType<string>(row.Values["Description"]));
    }

    [Fact]
    public async Task ReadAsync_DistinguishesExplicitEmptyAndMissingFields()
    {
        using var stream = CreateStream("A,B,C\nfirst,,third\nsecond");
        var extractor = new CsvFileExtractor();

        var rows = await ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None));

        Assert.Equal(string.Empty, Assert.IsType<string>(rows[0].Values["B"]));
        Assert.Null(rows[1].Values["B"]);
        Assert.Null(rows[1].Values["C"]);
        Assert.Equal(3, rows[1].Values.Count);
    }

    [Theory]
    [InlineData(null, ",")]
    [InlineData(CsvDelimiter.Comma, ",")]
    [InlineData(CsvDelimiter.Semicolon, ";")]
    [InlineData(CsvDelimiter.Tab, "\t")]
    public async Task ReadAsync_HonorsConfiguredDelimiter(CsvDelimiter? delimiter, string separator)
    {
        using var stream = CreateStream($"A{separator}B\nleft{separator}right");
        var extractor = new CsvFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions(delimiter), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal("left", Assert.IsType<string>(row.Values["A"]));
        Assert.Equal("right", Assert.IsType<string>(row.Values["B"]));
    }

    [Fact]
    public async Task ReadAsync_TreatsCaseDistinctHeadersAsDifferentFields()
    {
        using var stream = CreateStream("Name,name\nAda,Alias");
        var extractor = new CsvFileExtractor();

        var rows = await ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal("Ada", Assert.IsType<string>(row.Values["Name"]));
        Assert.Equal("Alias", Assert.IsType<string>(row.Values["name"]));
    }

    [Fact]
    public async Task ReadAsync_HeaderOnlyInputYieldsNoRows()
    {
        using var stream = CreateStream("A,B");
        var extractor = new CsvFileExtractor();

        var rows = await ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None));

        Assert.Empty(rows);
        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.NotEqual(-1, stream.ReadByte());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\n")]
    public async Task ReadAsync_RejectsInputWithoutAHeaderRecord(string content)
    {
        using var stream = CreateStream(content);
        var extractor = new CsvFileExtractor();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None)));
    }

    [Theory]
    [InlineData(",B\n1,2")]
    [InlineData("   ,B\n1,2")]
    [InlineData("A,A\n1,2")]
    public async Task ReadAsync_RejectsInvalidHeaders(string content)
    {
        using var stream = CreateStream(content);
        var extractor = new CsvFileExtractor();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_RejectsRecordsWiderThanTheHeader()
    {
        using var stream = CreateStream("A,B\n1,2,3");
        var extractor = new CsvFileExtractor();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_RejectsHeaderlessConfiguration()
    {
        using var stream = CreateStream("1,2");
        var extractor = new CsvFileExtractor();
        var options = CreateOptions();
        options.FirstRowIsHeader = false;

        await Assert.ThrowsAsync<NotSupportedException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, options, CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_RejectsUndefinedDelimiter()
    {
        using var stream = CreateStream("A,B\n1,2");
        var extractor = new CsvFileExtractor();
        var options = CreateOptions((CsvDelimiter)999);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, options, CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_TranslatesMalformedCsvAndPreservesParserException()
    {
        using var stream = CreateStream("A,B\n1,bad\"value");
        var extractor = new CsvFileExtractor();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None)));

        Assert.IsAssignableFrom<CsvHelperException>(exception.InnerException);
        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.NotEqual(-1, stream.ReadByte());
    }

    [Fact]
    public async Task ReadAsync_DoesNotTranslateStreamIoFailures()
    {
        using var stream = new ThrowingReadStream();
        var extractor = new CsvFileExtractor();

        await Assert.ThrowsAsync<IOException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_ObservesCancellationBetweenRecords()
    {
        using var stream = CreateStream("A,B\n1,2\n3,4");
        using var cancellationSource = new CancellationTokenSource();
        var extractor = new CsvFileExtractor();

        {
            await using var enumerator = extractor
                .ReadAsync(stream, CreateOptions(), cancellationSource.Token)
                .GetAsyncEnumerator();

            Assert.True(await enumerator.MoveNextAsync());
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await enumerator.MoveNextAsync();
            });
        }

        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.NotEqual(-1, stream.ReadByte());
    }

    [Fact]
    public async Task ReadAsync_AlreadyCancelledTokenPreventsReadingAndLeavesStreamOpen()
    {
        using var stream = new TrackingMemoryStream(Encoding.UTF8.GetBytes("A,B\n1,2"));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var extractor = new CsvFileExtractor();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions(), cancellationSource.Token)));

        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.NotEqual(-1, stream.ReadByte());
    }

    [Fact]
    public async Task ReadAsync_LeavesCallerStreamOpenAfterCompletion()
    {
        using var stream = CreateStream("A,B\n1,2");
        var extractor = new CsvFileExtractor();

        await ReadAllAsync(extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None));

        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.NotEqual(-1, stream.ReadByte());
    }

    [Fact]
    public async Task ReadAsync_LeavesCallerStreamOpenAfterEarlyDisposal()
    {
        using var stream = CreateStream("A,B\n1,2\n3,4");
        var extractor = new CsvFileExtractor();
        var enumerator = extractor
            .ReadAsync(stream, CreateOptions(), CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();

        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.NotEqual(-1, stream.ReadByte());
    }

    [Fact]
    public async Task ReadAsync_IsDeferredAndDoesNotConsumeTheWholeStreamForTheFirstRow()
    {
        var content = new StringBuilder("A,B\nfirst,row\n");

        for (var index = 0; index < 20_000; index++)
        {
            content.Append(index).Append(",value\n");
        }

        using var stream = new TrackingMemoryStream(Encoding.UTF8.GetBytes(content.ToString()));
        var extractor = new CsvFileExtractor();
        var rows = extractor.ReadAsync(stream, CreateOptions(), CancellationToken.None);

        Assert.Equal(0, stream.BytesRead);

        await using var enumerator = rows.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        Assert.Equal("first", Assert.IsType<string>(enumerator.Current.Values["A"]));
        Assert.InRange(stream.BytesRead, 1, stream.Length - 1);
    }

    private static SourceOptions CreateOptions(CsvDelimiter? delimiter = null)
    {
        return new SourceOptions { Delimiter = delimiter };
    }

    private static MemoryStream CreateStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static async Task<List<DataRow>> ReadAllAsync(IAsyncEnumerable<DataRow> rows)
    {
        var result = new List<DataRow>();

        await foreach (var row in rows)
        {
            result.Add(row);
        }

        return result;
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("Test stream read failure.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            throw new IOException("Test stream read failure.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Track(base.Read(buffer, offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            return Track(base.Read(buffer));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Track(base.Read(buffer, offset, count)));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Track(base.Read(buffer.Span)));
        }

        private int Track(int count)
        {
            BytesRead += count;
            return count;
        }
    }
}
