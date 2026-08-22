using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ConvertToDateTransformationHandler : ISourceDateFormatTransformationHandler
{
    public TransformationType Type => TransformationType.ConvertToDate;

    public DataRow Apply(DataRow row, TransformationRule rule) =>
        throw new InvalidOperationException(
            "The convert to date transformation requires the pipeline source culture and date format.");

    public DataRow Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture) =>
        Apply(row, rule, sourceCulture, dateFormat: null);

    public DataRow Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The convert to date transformation requires a non-empty source field.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The convert to date transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is null or DateTime)
        {
            return row;
        }

        if (value is not string text)
        {
            throw UnsupportedValue(value, rule.SourceField, row.SourceRowNumber);
        }

        if (!string.IsNullOrWhiteSpace(dateFormat)
            && IsTimezoneBearingStandardFormat(dateFormat))
        {
            throw UnsupportedTimezone(rule.SourceField, row.SourceRowNumber);
        }

        var parsed = string.IsNullOrWhiteSpace(dateFormat)
            ? Parse(text, sourceCulture, rule.SourceField, row.SourceRowNumber)
            : ParseExact(text, dateFormat, sourceCulture, rule.SourceField, row.SourceRowNumber);

        if (parsed.Kind is not DateTimeKind.Unspecified)
        {
            throw UnsupportedTimezone(rule.SourceField, row.SourceRowNumber);
        }

        row.Values[rule.SourceField] = parsed;
        return row;
    }

    private static DateTime Parse(
        string text,
        CultureInfo sourceCulture,
        string field,
        long rowNumber)
    {
        if (DateTime.TryParse(
                text,
                sourceCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed)
            && (parsed.Kind is not DateTimeKind.Unspecified
                || MatchesCulturePatternWithYear(text, parsed, sourceCulture)))
        {
            return parsed;
        }

        throw InvalidText(field, rowNumber, sourceCulture, dateFormat: null);
    }

    private static DateTime ParseExact(
        string text,
        string dateFormat,
        CultureInfo sourceCulture,
        string field,
        long rowNumber)
    {
        if (FormatContainsYear(dateFormat, sourceCulture)
            && DateTime.TryParseExact(
                text,
                dateFormat,
                sourceCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return parsed;
        }

        throw InvalidText(field, rowNumber, sourceCulture, dateFormat);
    }

    private static bool MatchesCulturePatternWithYear(
        string text,
        DateTime parsed,
        CultureInfo sourceCulture)
    {
        foreach (var pattern in GetCultureYearPatterns(sourceCulture))
        {
            if (DateTime.TryParseExact(
                    text,
                    pattern,
                    sourceCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var exact)
                && exact == parsed)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetCultureYearPatterns(CultureInfo sourceCulture)
    {
        var dateTimeFormat = sourceCulture.DateTimeFormat;
        var datePatterns = dateTimeFormat
            .GetAllDateTimePatterns('d')
            .Concat(dateTimeFormat.GetAllDateTimePatterns('D'))
            .Concat(dateTimeFormat.GetAllDateTimePatterns('y'))
            .SelectMany(pattern => ExpandDateSeparators(pattern, dateTimeFormat.DateSeparator))
            .Where(CustomFormatContainsYear)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var timePatterns = dateTimeFormat
            .GetAllDateTimePatterns('t')
            .Concat(dateTimeFormat.GetAllDateTimePatterns('T'))
            .Concat(["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"])
            .SelectMany(ExpandFractionalSeconds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var datePattern in datePatterns)
        {
            yield return datePattern;

            foreach (var timePattern in timePatterns)
            {
                yield return $"{datePattern} {timePattern}";
            }
        }
    }

    private static IEnumerable<string> ExpandFractionalSeconds(string pattern)
    {
        yield return pattern;

        var secondTokenEnd = FindSecondTokenEnd(pattern);
        if (secondTokenEnd >= 0)
        {
            yield return pattern.Insert(secondTokenEnd, ".FFFFFFF");
        }
    }

    private static int FindSecondTokenEnd(string format)
    {
        for (var index = 0; index < format.Length; index++)
        {
            var character = format[index];

            if (character is '\'' or '"')
            {
                var quote = character;
                while (++index < format.Length && format[index] != quote)
                {
                    if (format[index] == '\\' && index + 1 < format.Length)
                    {
                        index++;
                    }
                }

                continue;
            }

            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character == '%' && index + 1 < format.Length)
            {
                if (format[index + 1] == 's')
                {
                    return index + 2;
                }

                index++;
                continue;
            }

            if (character == 's')
            {
                while (index + 1 < format.Length && format[index + 1] == 's')
                {
                    index++;
                }

                return index + 1;
            }
        }

        return -1;
    }

    private static IEnumerable<string> ExpandDateSeparators(
        string pattern,
        string cultureDateSeparator)
    {
        yield return pattern;

        foreach (var separator in new[] { "/", ".", "-" })
        {
            var expanded = pattern.Replace(
                "/",
                $"'{separator}'",
                StringComparison.Ordinal);

            if (cultureDateSeparator != "/")
            {
                expanded = expanded.Replace(
                    cultureDateSeparator,
                    $"'{separator}'",
                    StringComparison.Ordinal);
            }

            yield return expanded;
        }
    }

    private static bool FormatContainsYear(
        string format,
        CultureInfo sourceCulture)
    {
        if (format.Length == 1)
        {
            try
            {
                return sourceCulture.DateTimeFormat
                    .GetAllDateTimePatterns(format[0])
                    .Any(CustomFormatContainsYear);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return CustomFormatContainsYear(format);
    }

    private static bool IsTimezoneBearingStandardFormat(string format) =>
        format.Length == 1 && format[0] is 'R' or 'r' or 'u';

    private static bool CustomFormatContainsYear(string format)
    {
        for (var index = 0; index < format.Length; index++)
        {
            var character = format[index];

            if (character is '\'' or '"')
            {
                var quote = character;
                while (++index < format.Length && format[index] != quote)
                {
                    if (format[index] == '\\' && index + 1 < format.Length)
                    {
                        index++;
                    }
                }

                continue;
            }

            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character == '%')
            {
                if (++index < format.Length && format[index] == 'y')
                {
                    return true;
                }

                continue;
            }

            if (character == 'y')
            {
                return true;
            }
        }

        return false;
    }

    private static FormatException InvalidText(
        string field,
        long rowNumber,
        CultureInfo sourceCulture,
        string? dateFormat) => string.IsNullOrWhiteSpace(dateFormat)
            ? new FormatException(
                $"The convert to date transformation field '{field}' in row {rowNumber} is not a valid date for culture '{sourceCulture.Name}'.")
            : new FormatException(
                $"The convert to date transformation field '{field}' in row {rowNumber} is not a valid date for culture '{sourceCulture.Name}' and format '{dateFormat}'.");

    private static InvalidOperationException UnsupportedValue(
        object value,
        string field,
        long rowNumber) => new(
            $"The convert to date transformation field '{field}' in row {rowNumber} has unsupported value type '{value.GetType().FullName}'.");

    private static FormatException UnsupportedTimezone(
        string field,
        long rowNumber) => new(
            $"The convert to date transformation field '{field}' in row {rowNumber} contains unsupported timezone or offset information.");
}
