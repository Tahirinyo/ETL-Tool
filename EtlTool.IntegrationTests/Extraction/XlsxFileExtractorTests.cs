using System.Text;
using EtlTool.Application.Extraction;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using ExcelDataReader.Exceptions;
using static EtlTool.IntegrationTests.Extraction.OpenXmlWorkbookFixture;

namespace EtlTool.IntegrationTests.Extraction;

public sealed class XlsxFileExtractorTests
{
    [Fact]
    public async Task ReadAsync_ReadsRepositoryWorkbookWithNativeValuesAndLogicalNumbers()
    {
        await using var stream = File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "TestData", "clean.xlsx"));
        IFileExtractor extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("Customers"), CancellationToken.None));

        Assert.NotEmpty(rows);
        Assert.Equal(2, rows[0].SourceRowNumber);
        Assert.Equal(
            ["CustomerId", "FullName", "Email", "Age", "Balance", "BirthDate", "Country"],
            rows[0].Values.Keys);
        Assert.Equal(1d, Assert.IsType<double>(rows[0].Values["CustomerId"]));
        Assert.Equal("Ahmet Yilmaz", Assert.IsType<string>(rows[0].Values["FullName"]));
    }

    [Fact]
    public async Task ReadAsync_SelectsOnlyRequestedWorksheet()
    {
        using var stream = Create(
            Sheet(
                "January",
                Row(Text(1, "Month"), Text(2, "Value")),
                Row(Text(1, "January"), Text(4, "unexpected-width"))),
            Sheet(
                "February",
                Row(Text(1, "Id"), Text(2, "Name")),
                Row(Text(1, "2"), Text(2, "Ayse"))),
            Sheet(
                "Summary",
                Row(Text(1, "Total")),
                Row(Number(1, 1))));
        IFileExtractor extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("February"), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal(["Id", "Name"], row.Values.Keys);
        Assert.Equal("2", row.Values["Id"]);
        Assert.Equal("Ayse", row.Values["Name"]);
    }

    [Theory]
    [InlineData("february")]
    [InlineData("FEBRUARY")]
    public async Task ReadAsync_MatchesWorksheetNamesCaseInsensitively(string worksheetName)
    {
        using var stream = Create(
            Sheet(
                "February",
                Row(Text(1, "Id")),
                Row(Text(1, "1"))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions(worksheetName), CancellationToken.None));

        Assert.Single(rows);
    }

    [Theory]
    [InlineData(" Şubat Özeti ")]
    [InlineData(" şubat özeti ")]
    public async Task ReadAsync_MatchesUnicodeAndSignificantSpacesWithoutCurrentCulture(string worksheetName)
    {
        using var stream = Create(
            HiddenSheet(
                " Şubat Özeti ",
                Row(Text(1, "Id")),
                Row(Text(1, "1"))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions(worksheetName), CancellationToken.None));

        Assert.Single(rows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadAsync_RejectsMissingWorksheetBeforeReading(string? worksheetName)
    {
        using var source = Create(
            Sheet("Sheet1", Row(Text(1, "Id")), Row(Text(1, "1"))));
        using var stream = new TrackingMemoryStream(source.ToArray());
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions(worksheetName), CancellationToken.None)));

        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_RejectsUnknownWorksheetAndLeavesStreamOpen()
    {
        using var stream = Create(
            Sheet("Known", Row(Text(1, "Id")), Row(Text(1, "1"))));
        var extractor = new XlsxFileExtractor();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Missing"), CancellationToken.None)));

        Assert.Contains("Missing", exception.Message, StringComparison.Ordinal);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReadAsync_RejectsEmptyWorksheet()
    {
        using var stream = Create(Sheet("Empty"));
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Empty"), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_RejectsBlankFirstRow()
    {
        using var stream = Create(
            Sheet("BlankHeader", Row(), Row(Text(1, "value"))));
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("BlankHeader"), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_HeaderOnlyWorksheetYieldsNoRows()
    {
        using var stream = Create(
            Sheet("HeaderOnly", Row(Text(1, "Id"), Text(2, "Name"))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("HeaderOnly"), CancellationToken.None));

        Assert.Empty(rows);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("whitespace")]
    [InlineData("duplicate")]
    public async Task ReadAsync_RejectsInvalidHeaders(string kind)
    {
        var headers = kind switch
        {
            "blank" => Row(Text(1, "Id"), Blank(2), Text(3, "Name")),
            "whitespace" => Row(Text(1, "Id"), Text(2, "   ")),
            "duplicate" => Row(Text(1, "Id"), Text(2, "Id")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var stream = Create(Sheet("Invalid", headers));
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Invalid"), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_UsesInvariantTextForNonStringHeadersAndKeepsCaseDistinctHeaders()
    {
        using var stream = Create(
            Sheet(
                "Headers",
                Row(Number(1, 42.5), Text(2, "Name"), Text(3, "name")),
                Row(Text(1, "value"), Text(2, "Ada"), Text(3, "Alias"))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("Headers"), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal(["42.5", "Name", "name"], row.Values.Keys);
        Assert.Equal("Ada", row.Values["Name"]);
        Assert.Equal("Alias", row.Values["name"]);
    }

    [Fact]
    public async Task ReadAsync_MapsSparseAndShortRowsAndSkipsNullOnlyRows()
    {
        using var stream = Create(
            Sheet(
                "Sparse",
                Row(Text(1, "A"), Text(2, "B"), Text(3, "C")),
                Row(Text(1, "first"), Text(3, "third")),
                Row(),
                Row(Text(1, "second")),
                Row(Text(1, "third"), SharedEmptyText(2), Text(3, "value"))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("Sparse"), CancellationToken.None));

        Assert.Equal([2L, 3L, 4L], rows.Select(row => row.SourceRowNumber));
        Assert.Null(rows[0].Values["B"]);
        Assert.Equal("third", rows[0].Values["C"]);
        Assert.Null(rows[1].Values["B"]);
        Assert.Null(rows[1].Values["C"]);
        Assert.Equal(string.Empty, Assert.IsType<string>(rows[2].Values["B"]));
    }

    [Fact]
    public async Task ReadAsync_IgnoresNullOnlyCellsBeyondHeaderWidth()
    {
        using var stream = Create(
            Sheet(
                "TrailingBlank",
                Row(Text(1, "A"), Text(2, "B")),
                Row(Text(1, "left"), Text(2, "right"), Blank(4))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("TrailingBlank"), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal(2, row.Values.Count);
    }

    [Fact]
    public async Task ReadAsync_RejectsPopulatedCellsBeyondHeaderWidth()
    {
        using var stream = Create(
            Sheet(
                "Wide",
                Row(Text(1, "A"), Text(2, "B")),
                Row(Text(1, "left"), Text(2, "right"), Text(4, "extra"))));
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Wide"), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_PreservesNativeCellTypesAndBlankValues()
    {
        using var stream = Create(
            Sheet(
                "Types",
                Row(
                    Text(1, "Text"),
                    Text(2, "Number"),
                    Text(3, "Boolean"),
                    Text(4, "Date"),
                    Text(5, "Time"),
                    Text(6, "Blank")),
                Row(
                    Text(1, "value"),
                    Number(2, 12.5),
                    Boolean(3, true),
                    Number(4, 45292, styleIndex: 1),
                    Number(5, 0.5, styleIndex: 2),
                    Blank(6))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("Types"), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal("value", Assert.IsType<string>(row.Values["Text"]));
        Assert.Equal(12.5, Assert.IsType<double>(row.Values["Number"]));
        Assert.True(Assert.IsType<bool>(row.Values["Boolean"]));
        Assert.Equal(new DateTime(2024, 1, 1), Assert.IsType<DateTime>(row.Values["Date"]));
        Assert.Equal(TimeSpan.FromHours(12), Assert.IsType<TimeSpan>(row.Values["Time"]));
        Assert.Null(row.Values["Blank"]);
    }

    [Fact]
    public async Task ReadAsync_ExposesCachedFormulaValueAndDoesNotCalculateMissingCache()
    {
        using var stream = Create(
            Sheet(
                "Formulas",
                Row(Text(1, "Cached"), Text(2, "NoCache")),
                Row(Formula(1, "1+2", "3"), Formula(2, "2+2", cachedValue: null))));
        var extractor = new XlsxFileExtractor();

        var rows = await ReadAllAsync(
            extractor.ReadAsync(stream, CreateOptions("Formulas"), CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal(3d, Assert.IsType<double>(row.Values["Cached"]));
        Assert.Null(row.Values["NoCache"]);
    }

    [Fact]
    public async Task ReadAsync_TranslatesMalformedWorkbookAndPreservesParserException()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not-an-xlsx-workbook"));
        var extractor = new XlsxFileExtractor();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Sheet1"), CancellationToken.None)));

        Assert.IsAssignableFrom<ExcelReaderException>(exception.InnerException);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReadAsync_RejectsNonSeekableStreamBeforeReading()
    {
        using var source = Create(
            Sheet("Sheet1", Row(Text(1, "Id")), Row(Text(1, "1"))));
        using var stream = new NonSeekableReadStream(source.ToArray());
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Sheet1"), CancellationToken.None)));

        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_DoesNotTranslateStreamIoFailures()
    {
        using var stream = new ThrowingSeekableReadStream();
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAsync<IOException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Sheet1"), CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_RejectsHeaderlessConfiguration()
    {
        using var stream = Create(
            Sheet("Sheet1", Row(Text(1, "1")), Row(Text(1, "2"))));
        var extractor = new XlsxFileExtractor();
        var options = CreateOptions("Sheet1");
        options.FirstRowIsHeader = false;

        await Assert.ThrowsAsync<NotSupportedException>(
            () => ReadAllAsync(extractor.ReadAsync(stream, options, CancellationToken.None)));
    }

    [Fact]
    public async Task ReadAsync_AlreadyCancelledTokenPreventsReadingAndLeavesStreamOpen()
    {
        using var source = Create(
            Sheet("Sheet1", Row(Text(1, "Id")), Row(Text(1, "1"))));
        using var stream = new TrackingMemoryStream(source.ToArray());
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var extractor = new XlsxFileExtractor();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAllAsync(
                extractor.ReadAsync(stream, CreateOptions("Sheet1"), cancellationSource.Token)));

        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReadAsync_ObservesCancellationBetweenRowsAndLeavesStreamOpen()
    {
        using var stream = Create(
            Sheet(
                "Sheet1",
                Row(Text(1, "Id")),
                Row(Text(1, "1")),
                Row(Text(1, "2"))));
        using var cancellationSource = new CancellationTokenSource();
        var extractor = new XlsxFileExtractor();

        await using (var enumerator = extractor
            .ReadAsync(stream, CreateOptions("Sheet1"), cancellationSource.Token)
            .GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await enumerator.MoveNextAsync();
            });
        }

        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReadAsync_LeavesCallerStreamOpenAfterCompletionAndEarlyDisposal()
    {
        using var completedStream = Create(
            Sheet("Sheet1", Row(Text(1, "Id")), Row(Text(1, "1"))));
        using var partialStream = Create(
            Sheet(
                "Sheet1",
                Row(Text(1, "Id")),
                Row(Text(1, "1")),
                Row(Text(1, "2"))));
        var extractor = new XlsxFileExtractor();

        await ReadAllAsync(
            extractor.ReadAsync(completedStream, CreateOptions("Sheet1"), CancellationToken.None));

        var enumerator = extractor
            .ReadAsync(partialStream, CreateOptions("Sheet1"), CancellationToken.None)
            .GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();

        Assert.True(completedStream.CanRead);
        Assert.True(partialStream.CanRead);
    }

    [Fact]
    public async Task ReadAsync_IsDeferred()
    {
        using var source = Create(
            Sheet("Sheet1", Row(Text(1, "Id")), Row(Text(1, "1"))));
        using var stream = new TrackingMemoryStream(source.ToArray());
        var extractor = new XlsxFileExtractor();

        var rows = extractor.ReadAsync(stream, CreateOptions("Sheet1"), CancellationToken.None);

        Assert.Equal(0, stream.BytesRead);

        await using var enumerator = rows.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(stream.BytesRead > 0);
    }

    [Fact]
    public async Task CsvAndXlsxCanBeConsumedThroughTheSameRowContract()
    {
        using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes("Id,Name\n1,Ada"));
        using var xlsxStream = Create(
            Sheet(
                "Sheet1",
                Row(Text(1, "Id"), Text(2, "Name")),
                Row(Text(1, "1"), Text(2, "Ada"))));
        IFileExtractor csvExtractor = new CsvFileExtractor();
        IFileExtractor xlsxExtractor = new XlsxFileExtractor();

        var csvRows = await ReadAllAsync(
            csvExtractor.ReadAsync(csvStream, new SourceOptions(), CancellationToken.None));
        var xlsxRows = await ReadAllAsync(
            xlsxExtractor.ReadAsync(xlsxStream, CreateOptions("Sheet1"), CancellationToken.None));

        Assert.Equal(csvRows[0].SourceRowNumber, xlsxRows[0].SourceRowNumber);
        Assert.Equal(csvRows[0].Values.Keys, xlsxRows[0].Values.Keys);
        Assert.Equal(csvRows[0].Values, xlsxRows[0].Values);
    }

    private static SourceOptions CreateOptions(string? worksheetName)
    {
        return new SourceOptions { WorksheetName = worksheetName };
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

        private int Track(int count)
        {
            BytesRead += count;
            return count;
        }
    }

    private sealed class NonSeekableReadStream(byte[] buffer) : MemoryStream(buffer)
    {
        public long BytesRead { get; private set; }

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] destination, int offset, int count)
        {
            var read = base.Read(destination, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> destination)
        {
            var read = base.Read(destination);
            BytesRead += read;
            return read;
        }
    }

    private sealed class ThrowingSeekableReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => 16;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("Test stream read failure.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            return Position;
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
}
