using System.Globalization;
using EtlTool.Domain.Enums;

namespace EtlTool.Domain.ValueObjects;

public sealed class SourceOptions
{
    private static readonly CultureInfo[] SpecificCultures =
        CultureInfo.GetCultures(CultureTypes.SpecificCultures);

    public string CultureName { get; set; } = string.Empty;

    public string? DateFormat { get; set; }

    public CsvDelimiter? Delimiter { get; set; }

    public string? WorksheetName { get; set; }

    public bool FirstRowIsHeader { get; set; } = true;

    public CultureInfo ResolveCulture()
    {
        if (string.IsNullOrEmpty(CultureName))
        {
            return CultureInfo.InvariantCulture;
        }

        var culture = Array.Find(
            SpecificCultures,
            candidate => string.Equals(
                candidate.Name,
                CultureName,
                StringComparison.OrdinalIgnoreCase));

        if (culture is null)
        {
            throw new CultureNotFoundException(
                nameof(CultureName),
                CultureName,
                "The source culture must be an installed specific culture.");
        }

        return CultureInfo.GetCultureInfo(culture.Name);
    }
}
