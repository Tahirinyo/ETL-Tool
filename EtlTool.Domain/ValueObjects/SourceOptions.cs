using EtlTool.Domain.Enums;

namespace EtlTool.Domain.ValueObjects;

public sealed class SourceOptions
{
    public string CultureName { get; set; } = string.Empty;

    public string? DateFormat { get; set; }

    public CsvDelimiter? Delimiter { get; set; }

    public string? WorksheetName { get; set; }

    public bool FirstRowIsHeader { get; set; } = true;
}
