using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Sources;

public sealed class SourceSchemaInferenceService
{
    public IReadOnlyList<SourceFieldDefinition> Infer(
        IReadOnlyList<string> columns,
        IReadOnlyList<DataRow> sampleRows,
        SourceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(sampleRows);
        ArgumentNullException.ThrowIfNull(options);

        var culture = options.ResolveCulture();
        var schema = new List<SourceFieldDefinition>(columns.Count);

        foreach (var column in columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suggestion = SourceFieldType.Unknown;

            foreach (var row in sampleRows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!row.Values.TryGetValue(column, out var value) || IsEmpty(value))
                {
                    continue;
                }

                suggestion = Combine(
                    suggestion,
                    Classify(value!, culture, options.DateFormat));

                if (suggestion == SourceFieldType.String)
                {
                    break;
                }
            }

            schema.Add(new SourceFieldDefinition { Name = column, DataType = suggestion });
        }

        return schema;
    }

    private static bool IsEmpty(object? value) =>
        value is null or DBNull || value is string text && string.IsNullOrWhiteSpace(text);

    private static SourceFieldType Classify(
        object value,
        CultureInfo culture,
        string? dateFormat)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return SourceFieldType.Integer;
            case decimal number:
                return decimal.Truncate(number) == number
                    ? SourceFieldType.Integer
                    : SourceFieldType.Decimal;
            case double number when double.IsFinite(number):
                return Math.Truncate(number) == number
                    ? SourceFieldType.Integer
                    : SourceFieldType.Decimal;
            case float number when float.IsFinite(number):
                return MathF.Truncate(number) == number
                    ? SourceFieldType.Integer
                    : SourceFieldType.Decimal;
            case DateTime or DateTimeOffset:
                return SourceFieldType.Date;
            case string text:
                return ClassifyText(text, culture, dateFormat);
            default:
                return SourceFieldType.String;
        }
    }

    private static SourceFieldType ClassifyText(
        string text,
        CultureInfo culture,
        string? dateFormat)
    {
        if (long.TryParse(
                text,
                NumberStyles.Integer | NumberStyles.AllowThousands,
                culture,
                out _))
        {
            return SourceFieldType.Integer;
        }

        if (decimal.TryParse(text, NumberStyles.Number, culture, out _))
        {
            return SourceFieldType.Decimal;
        }

        var dateIsValid = string.IsNullOrWhiteSpace(dateFormat)
            ? DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out _)
            : DateTime.TryParseExact(text, dateFormat, culture, DateTimeStyles.AllowWhiteSpaces, out _);

        return dateIsValid ? SourceFieldType.Date : SourceFieldType.String;
    }

    private static SourceFieldType Combine(SourceFieldType current, SourceFieldType evidence)
    {
        if (current == SourceFieldType.Unknown)
        {
            return evidence;
        }

        if (current == SourceFieldType.String || evidence == SourceFieldType.String)
        {
            return SourceFieldType.String;
        }

        if (current is SourceFieldType.Integer or SourceFieldType.Decimal
            && evidence is SourceFieldType.Integer or SourceFieldType.Decimal)
        {
            return current == SourceFieldType.Decimal || evidence == SourceFieldType.Decimal
                ? SourceFieldType.Decimal
                : SourceFieldType.Integer;
        }

        return current == evidence ? current : SourceFieldType.String;
    }
}
