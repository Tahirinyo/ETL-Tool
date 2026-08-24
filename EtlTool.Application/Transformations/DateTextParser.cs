using System.Globalization;

namespace EtlTool.Application.Transformations;

internal static class DateTextParser
{
    public static DateTime Parse(
        string text,
        CultureInfo sourceCulture,
        string? dateFormat,
        string errorSubject)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceCulture);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorSubject);

        if (!string.IsNullOrWhiteSpace(dateFormat))
        {
            ValidateFormat(dateFormat, sourceCulture);
        }

        var parsed = string.IsNullOrWhiteSpace(dateFormat)
            ? ParseUsingCulture(text, sourceCulture, errorSubject)
            : ParseExact(text, dateFormat, sourceCulture, errorSubject);

        return parsed.Kind is DateTimeKind.Unspecified
            ? parsed
            : throw UnsupportedTimezone(errorSubject);
    }

    internal static void ValidateFormat(
        string? dateFormat,
        CultureInfo sourceCulture)
    {
        ArgumentNullException.ThrowIfNull(sourceCulture);

        if (string.IsNullOrWhiteSpace(dateFormat))
        {
            return;
        }

        if (IsTimezoneBearingStandardFormat(dateFormat))
        {
            throw UnsupportedTimezone("The source date format");
        }

        if (dateFormat.Length > 1)
        {
            ValidateCustomFormatSyntax(dateFormat, sourceCulture);

            if (CustomFormatContainsTimezoneSpecifier(dateFormat))
            {
                throw UnsupportedTimezone("The source date format");
            }
        }

        if (!FormatContainsYear(dateFormat, sourceCulture))
        {
            throw new FormatException("The source date format must include a year.");
        }
    }

    private static DateTime ParseUsingCulture(
        string text,
        CultureInfo sourceCulture,
        string errorSubject)
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

        throw InvalidText(errorSubject, sourceCulture, dateFormat: null);
    }

    private static DateTime ParseExact(
        string text,
        string dateFormat,
        CultureInfo sourceCulture,
        string errorSubject)
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

        throw InvalidText(errorSubject, sourceCulture, dateFormat);
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

    private static void ValidateCustomFormatSyntax(
        string format,
        CultureInfo sourceCulture)
    {
        try
        {
            _ = new DateTime(2000, 1, 1).ToString(format, sourceCulture);
        }
        catch (FormatException exception)
        {
            throw new FormatException("The source date format is not a valid custom date format.", exception);
        }
    }

    private static bool CustomFormatContainsTimezoneSpecifier(string format)
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

            if (character is 'z' or 'K')
            {
                return true;
            }
        }

        return false;
    }

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
        string errorSubject,
        CultureInfo sourceCulture,
        string? dateFormat) => string.IsNullOrWhiteSpace(dateFormat)
            ? new FormatException(
                $"{errorSubject} is not a valid date for culture '{sourceCulture.Name}'.")
            : new FormatException(
                $"{errorSubject} is not a valid date for culture '{sourceCulture.Name}' and format '{dateFormat}'.");

    private static FormatException UnsupportedTimezone(string errorSubject) => new(
        $"{errorSubject} contains unsupported timezone or offset information.");
}
