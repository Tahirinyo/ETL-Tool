using System.Text;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using static EtlTool.IntegrationTests.Extraction.OpenXmlWorkbookFixture;

namespace EtlTool.IntegrationTests.Extraction;

public sealed class SourceHeaderTests
{
    [Fact]
    public async Task CsvHeaders_UseSelectedDelimiterWithoutDataRows()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Id;Name"));
        var headers = await new CsvFileExtractor().ReadHeadersAsync(
            stream, new SourceOptions { Delimiter = CsvDelimiter.Semicolon }, CancellationToken.None);
        Assert.Equal(["Id", "Name"], headers);
    }

    [Fact]
    public async Task XlsxHeadersAndWorksheetNames_AreAvailableWithoutDataRows()
    {
        await using var stream = Create(Sheet("First", Row(Text(1, "Id"), Text(2, "Name"))), Sheet("Second", Row(Text(1, "Code"))));
        var extractor = new XlsxFileExtractor();
        var names = await extractor.GetWorksheetNamesAsync(stream, CancellationToken.None);
        stream.Position = 0;
        var headers = await extractor.ReadHeadersAsync(stream, new SourceOptions { WorksheetName = "Second" }, CancellationToken.None);
        Assert.Equal(["First", "Second"], names);
        Assert.Equal(["Code"], headers);
    }
}
