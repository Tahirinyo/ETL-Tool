using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace EtlTool.IntegrationTests.Extraction;

internal static class OpenXmlWorkbookFixture
{
    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypeNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    public static MemoryStream Create(params Worksheet[] worksheets)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteDocument(archive, "[Content_Types].xml", CreateContentTypes(worksheets.Length));
            WriteDocument(archive, "_rels/.rels", CreateRootRelationships());
            WriteDocument(archive, "xl/workbook.xml", CreateWorkbook(worksheets));
            WriteDocument(archive, "xl/_rels/workbook.xml.rels", CreateWorkbookRelationships(worksheets.Length));
            WriteDocument(archive, "xl/styles.xml", CreateStyles());
            WriteDocument(archive, "xl/sharedStrings.xml", CreateSharedStrings());

            for (var index = 0; index < worksheets.Length; index++)
            {
                WriteDocument(
                    archive,
                    $"xl/worksheets/sheet{index + 1}.xml",
                    CreateWorksheet(worksheets[index]));
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static Worksheet Sheet(string name, params RowDefinition[] rows)
    {
        return new Worksheet(name, rows, Hidden: false);
    }

    public static Worksheet HiddenSheet(string name, params RowDefinition[] rows)
    {
        return new Worksheet(name, rows, Hidden: true);
    }

    public static RowDefinition Row(params Cell[] cells)
    {
        return new RowDefinition(cells);
    }

    public static Cell Text(int column, string value)
    {
        return new Cell(column, CellKind.Text, value, StyleIndex: 0, Formula: null, HasCachedValue: true);
    }

    public static Cell Number(int column, double value, uint styleIndex = 0)
    {
        return new Cell(
            column,
            CellKind.Number,
            value.ToString("R", CultureInfo.InvariantCulture),
            styleIndex,
            Formula: null,
            HasCachedValue: true);
    }

    public static Cell Boolean(int column, bool value)
    {
        return new Cell(column, CellKind.Boolean, value ? "1" : "0", StyleIndex: 0, Formula: null, HasCachedValue: true);
    }

    public static Cell Blank(int column)
    {
        return new Cell(column, CellKind.Blank, Value: null, StyleIndex: 0, Formula: null, HasCachedValue: false);
    }

    public static Cell Formula(int column, string expression, string? cachedValue)
    {
        return new Cell(
            column,
            CellKind.Number,
            cachedValue,
            StyleIndex: 0,
            expression,
            HasCachedValue: cachedValue is not null);
    }

    public static Cell SharedEmptyText(int column)
    {
        return new Cell(
            column,
            CellKind.SharedString,
            "0",
            StyleIndex: 0,
            Formula: null,
            HasCachedValue: true);
    }

    private static XDocument CreateContentTypes(int worksheetCount)
    {
        var root = new XElement(
            ContentTypeNamespace + "Types",
            new XElement(
                ContentTypeNamespace + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(
                ContentTypeNamespace + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")),
            new XElement(
                ContentTypeNamespace + "Override",
                new XAttribute("PartName", "/xl/workbook.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(
                ContentTypeNamespace + "Override",
                new XAttribute("PartName", "/xl/styles.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
            new XElement(
                ContentTypeNamespace + "Override",
                new XAttribute("PartName", "/xl/sharedStrings.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml")));

        for (var index = 1; index <= worksheetCount; index++)
        {
            root.Add(
                new XElement(
                    ContentTypeNamespace + "Override",
                    new XAttribute("PartName", $"/xl/worksheets/sheet{index}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
    }

    private static XDocument CreateRootRelationships()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                PackageRelationshipNamespace + "Relationships",
                new XElement(
                    PackageRelationshipNamespace + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
    }

    private static XDocument CreateWorkbook(IReadOnlyList<Worksheet> worksheets)
    {
        var sheets = new XElement(SpreadsheetNamespace + "sheets");

        for (var index = 0; index < worksheets.Count; index++)
        {
            var worksheet = worksheets[index];
            var sheet = new XElement(
                SpreadsheetNamespace + "sheet",
                new XAttribute("name", worksheet.Name),
                new XAttribute("sheetId", index + 1),
                new XAttribute(RelationshipNamespace + "id", $"rId{index + 1}"));

            if (worksheet.Hidden)
            {
                sheet.Add(new XAttribute("state", "hidden"));
            }

            sheets.Add(sheet);
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                SpreadsheetNamespace + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelationshipNamespace),
                sheets));
    }

    private static XDocument CreateWorkbookRelationships(int worksheetCount)
    {
        var relationships = new XElement(PackageRelationshipNamespace + "Relationships");

        for (var index = 1; index <= worksheetCount; index++)
        {
            relationships.Add(
                new XElement(
                    PackageRelationshipNamespace + "Relationship",
                    new XAttribute("Id", $"rId{index}"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", $"worksheets/sheet{index}.xml")));
        }

        relationships.Add(
            new XElement(
                PackageRelationshipNamespace + "Relationship",
                new XAttribute("Id", $"rId{worksheetCount + 1}"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                new XAttribute("Target", "styles.xml")));
        relationships.Add(
            new XElement(
                PackageRelationshipNamespace + "Relationship",
                new XAttribute("Id", $"rId{worksheetCount + 2}"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"),
                new XAttribute("Target", "sharedStrings.xml")));

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), relationships);
    }

    private static XDocument CreateStyles()
    {
        XElement CreateFormat(uint numberFormatId)
        {
            return new XElement(
                SpreadsheetNamespace + "xf",
                new XAttribute("numFmtId", numberFormatId),
                new XAttribute("fontId", 0),
                new XAttribute("fillId", 0),
                new XAttribute("borderId", 0),
                new XAttribute("xfId", 0),
                new XAttribute("applyNumberFormat", 1));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                SpreadsheetNamespace + "styleSheet",
                new XElement(
                    SpreadsheetNamespace + "fonts",
                    new XAttribute("count", 1),
                    new XElement(SpreadsheetNamespace + "font")),
                new XElement(
                    SpreadsheetNamespace + "fills",
                    new XAttribute("count", 1),
                    new XElement(
                        SpreadsheetNamespace + "fill",
                        new XElement(
                            SpreadsheetNamespace + "patternFill",
                            new XAttribute("patternType", "none")))),
                new XElement(
                    SpreadsheetNamespace + "borders",
                    new XAttribute("count", 1),
                    new XElement(SpreadsheetNamespace + "border")),
                new XElement(
                    SpreadsheetNamespace + "cellStyleXfs",
                    new XAttribute("count", 1),
                    CreateFormat(0)),
                new XElement(
                    SpreadsheetNamespace + "cellXfs",
                    new XAttribute("count", 3),
                    CreateFormat(0),
                    CreateFormat(14),
                    CreateFormat(46))));
    }

    private static XDocument CreateSharedStrings()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                SpreadsheetNamespace + "sst",
                new XAttribute("count", 1),
                new XAttribute("uniqueCount", 1),
                new XElement(
                    SpreadsheetNamespace + "si",
                    new XElement(
                        SpreadsheetNamespace + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        string.Empty))));
    }

    private static XDocument CreateWorksheet(Worksheet worksheet)
    {
        var sheetData = new XElement(SpreadsheetNamespace + "sheetData");

        for (var rowIndex = 0; rowIndex < worksheet.Rows.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            var row = new XElement(
                SpreadsheetNamespace + "row",
                new XAttribute("r", rowNumber));

            foreach (var cell in worksheet.Rows[rowIndex].Cells.OrderBy(cell => cell.Column))
            {
                row.Add(CreateCell(cell, rowNumber));
            }

            sheetData.Add(row);
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(SpreadsheetNamespace + "worksheet", sheetData));
    }

    private static XElement CreateCell(Cell cell, int rowNumber)
    {
        var element = new XElement(
            SpreadsheetNamespace + "c",
            new XAttribute("r", $"{GetColumnName(cell.Column)}{rowNumber}"));

        if (cell.StyleIndex > 0)
        {
            element.Add(new XAttribute("s", cell.StyleIndex));
        }

        if (cell.Formula is not null)
        {
            element.Add(new XElement(SpreadsheetNamespace + "f", cell.Formula));
        }

        switch (cell.Kind)
        {
            case CellKind.Text:
                element.Add(new XAttribute("t", "inlineStr"));
                element.Add(
                    new XElement(
                        SpreadsheetNamespace + "is",
                        new XElement(
                            SpreadsheetNamespace + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            cell.Value ?? string.Empty)));
                break;
            case CellKind.Boolean:
                element.Add(new XAttribute("t", "b"));
                element.Add(new XElement(SpreadsheetNamespace + "v", cell.Value));
                break;
            case CellKind.Number when cell.HasCachedValue:
                element.Add(new XElement(SpreadsheetNamespace + "v", cell.Value));
                break;
            case CellKind.SharedString:
                element.Add(new XAttribute("t", "s"));
                element.Add(new XElement(SpreadsheetNamespace + "v", cell.Value));
                break;
            case CellKind.Blank:
                break;
            default:
                break;
        }

        return element;
    }

    private static string GetColumnName(int column)
    {
        if (column < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        var result = string.Empty;

        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }

        return result;
    }

    private static void WriteDocument(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        document.Save(entryStream);
    }

    internal sealed record Worksheet(string Name, IReadOnlyList<RowDefinition> Rows, bool Hidden);

    internal sealed record RowDefinition(IReadOnlyList<Cell> Cells);

    internal sealed record Cell(
        int Column,
        CellKind Kind,
        string? Value,
        uint StyleIndex,
        string? Formula,
        bool HasCachedValue);

    internal enum CellKind
    {
        Blank,
        Text,
        Number,
        Boolean,
        SharedString
    }
}
