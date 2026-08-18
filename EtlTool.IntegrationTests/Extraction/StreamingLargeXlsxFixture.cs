using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace EtlTool.IntegrationTests.Extraction;

internal static class StreamingLargeXlsxFixture
{
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypeNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly string[] Headers =
    [
        "CustomerId",
        "FullName",
        "Email",
        "Age",
        "Balance",
        "BirthDate",
        "Country"
    ];

    public const string WorksheetName = "Performance";

    public static void Create(string path, int dataRowCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (dataRowCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dataRowCount));
        }

        using var fileStream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        WriteContentTypes(archive);
        WriteRootRelationships(archive);
        WriteWorkbook(archive);
        WriteWorkbookRelationships(archive);
        WriteStyles(archive);
        WriteSharedStrings(archive, dataRowCount);
        WriteWorksheet(archive, dataRowCount);
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        using var writer = CreateWriter(archive, "[Content_Types].xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("Types", ContentTypeNamespace);
        WriteContentTypeDefault(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        WriteContentTypeDefault(writer, "xml", "application/xml");
        WriteContentTypeOverride(
            writer,
            "/xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteContentTypeOverride(
            writer,
            "/xl/styles.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        WriteContentTypeOverride(
            writer,
            "/xl/sharedStrings.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml");
        WriteContentTypeOverride(
            writer,
            "/xl/worksheets/sheet1.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        using var writer = CreateWriter(archive, "_rels/.rels");
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        WriteRelationship(
            writer,
            "rId1",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
            "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbook(ZipArchive archive)
    {
        using var writer = CreateWriter(archive, "xl/workbook.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipNamespace);
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", WorksheetName);
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", OfficeRelationshipNamespace, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        using var writer = CreateWriter(archive, "xl/_rels/workbook.xml.rels");
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        WriteRelationship(
            writer,
            "rId1",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
            "worksheets/sheet1.xml");
        WriteRelationship(
            writer,
            "rId2",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles",
            "styles.xml");
        WriteRelationship(
            writer,
            "rId3",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings",
            "sharedStrings.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteStyles(ZipArchive archive)
    {
        using var writer = CreateWriter(archive, "xl/styles.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("styleSheet", SpreadsheetNamespace);

        writer.WriteStartElement("fonts", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("font", SpreadsheetNamespace);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("fills", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("fill", SpreadsheetNamespace);
        writer.WriteStartElement("patternFill", SpreadsheetNamespace);
        writer.WriteAttributeString("patternType", "none");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("borders", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border", SpreadsheetNamespace);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        WriteCellFormat(writer, numberFormatId: 0, applyNumberFormat: false);
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "2");
        WriteCellFormat(writer, numberFormatId: 0, applyNumberFormat: false);
        WriteCellFormat(writer, numberFormatId: 14, applyNumberFormat: true);
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteSharedStrings(ZipArchive archive, int dataRowCount)
    {
        using var writer = CreateWriter(archive, "xl/sharedStrings.xml");
        var uniqueCount = Headers.Length + 1 + (dataRowCount * 2);
        var referenceCount = Headers.Length + (dataRowCount * 3);

        writer.WriteStartDocument();
        writer.WriteStartElement("sst", SpreadsheetNamespace);
        writer.WriteAttributeString("count", referenceCount.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("uniqueCount", uniqueCount.ToString(CultureInfo.InvariantCulture));

        foreach (var header in Headers)
        {
            WriteSharedString(writer, header);
        }

        WriteSharedString(writer, "Turkey");

        for (var row = 1; row <= dataRowCount; row++)
        {
            WriteSharedString(writer, $"Performance User {row}");
            WriteSharedString(writer, $"user{row}@example.com");
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorksheet(ZipArchive archive, int dataRowCount)
    {
        using var writer = CreateWriter(archive, "xl/worksheets/sheet1.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("dimension", SpreadsheetNamespace);
        writer.WriteAttributeString("ref", $"A1:G{dataRowCount + 1}");
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData", SpreadsheetNamespace);

        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", "1");
        for (var column = 0; column < Headers.Length; column++)
        {
            WriteSharedStringCell(writer, column, rowNumber: 1, sharedStringIndex: column);
        }

        writer.WriteEndElement();

        for (var dataRow = 1; dataRow <= dataRowCount; dataRow++)
        {
            var rowNumber = dataRow + 1;
            var nameIndex = Headers.Length + 1 + ((dataRow - 1) * 2);
            var date = new DateTime(1970 + (dataRow % 30), (dataRow % 12) + 1, (dataRow % 28) + 1);
            var balance = 1_000m + ((dataRow * 37) % 100_000) / 100m;

            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
            WriteNumberCell(writer, 0, rowNumber, dataRow.ToString(CultureInfo.InvariantCulture));
            WriteSharedStringCell(writer, 1, rowNumber, nameIndex);
            WriteSharedStringCell(writer, 2, rowNumber, nameIndex + 1);
            WriteNumberCell(writer, 3, rowNumber, (18 + (dataRow % 63)).ToString(CultureInfo.InvariantCulture));
            WriteNumberCell(writer, 4, rowNumber, balance.ToString(CultureInfo.InvariantCulture));
            WriteNumberCell(writer, 5, rowNumber, date.ToOADate().ToString("R", CultureInfo.InvariantCulture), styleIndex: 1);
            WriteSharedStringCell(writer, 6, rowNumber, Headers.Length);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static XmlWriter CreateWriter(ZipArchive archive, string path)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return XmlWriter.Create(
            entry.Open(),
            new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                CloseOutput = true,
                Indent = false
            });
    }

    private static void WriteContentTypeDefault(XmlWriter writer, string extension, string contentType)
    {
        writer.WriteStartElement("Default", ContentTypeNamespace);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteContentTypeOverride(XmlWriter writer, string partName, string contentType)
    {
        writer.WriteStartElement("Override", ContentTypeNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private static void WriteCellFormat(XmlWriter writer, uint numberFormatId, bool applyNumberFormat)
    {
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", numberFormatId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fontId", "0");
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteAttributeString("xfId", "0");
        if (applyNumberFormat)
        {
            writer.WriteAttributeString("applyNumberFormat", "1");
        }

        writer.WriteEndElement();
    }

    private static void WriteSharedString(XmlWriter writer, string value)
    {
        writer.WriteStartElement("si", SpreadsheetNamespace);
        writer.WriteStartElement("t", SpreadsheetNamespace);
        writer.WriteString(value);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSharedStringCell(
        XmlWriter writer,
        int zeroBasedColumn,
        int rowNumber,
        int sharedStringIndex)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", $"{GetColumnName(zeroBasedColumn)}{rowNumber}");
        writer.WriteAttributeString("t", "s");
        writer.WriteElementString("v", SpreadsheetNamespace, sharedStringIndex.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static void WriteNumberCell(
        XmlWriter writer,
        int zeroBasedColumn,
        int rowNumber,
        string value,
        uint? styleIndex = null)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", $"{GetColumnName(zeroBasedColumn)}{rowNumber}");
        if (styleIndex.HasValue)
        {
            writer.WriteAttributeString("s", styleIndex.Value.ToString(CultureInfo.InvariantCulture));
        }

        writer.WriteElementString("v", SpreadsheetNamespace, value);
        writer.WriteEndElement();
    }

    private static char GetColumnName(int zeroBasedColumn)
    {
        return checked((char)('A' + zeroBasedColumn));
    }
}
