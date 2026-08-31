using System.Globalization;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.PostgreSql;

/// <summary>
/// Converts PostgreSQL catalog type identities into the shared, intentionally coarse source schema.
/// </summary>
public sealed class PostgreSqlSourceSchemaConverter
{
    private const int MinimumNumericPrecision = 1;
    private const int MaximumNumericPrecision = 1_000;
    private const int MinimumNumericScale = -1_000;
    private const int MaximumNumericScale = 1_000;
    private const int MinimumCharacterLength = 1;
    private const int MaximumCharacterLength = 10_485_760;
    private const int MinimumTimestampPrecision = 0;
    private const int MaximumTimestampPrecision = 6;

    public IReadOnlyList<SourceFieldDefinition> Convert(
        IReadOnlyList<PostgreSqlColumnMetadata> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var schema = new List<SourceFieldDefinition>(columns.Count);
        foreach (var column in columns
            .OrderBy(column => column.OrdinalPosition)
            .ThenBy(column => column.Name, StringComparer.Ordinal)
            .ThenBy(column => column.NativeType, StringComparer.Ordinal))
        {
            var dataType = Map(column);
            schema.Add(new SourceFieldDefinition
            {
                Name = column.Name,
                DataType = dataType
            });
        }

        return schema;
    }

    private static SourceFieldType Map(PostgreSqlColumnMetadata column)
    {
        var nativeType = NormalizeWhitespace(column.NativeType);

        if (IsExact(nativeType, "smallint")
            || IsExact(nativeType, "integer")
            || IsExact(nativeType, "bigint"))
        {
            return SourceFieldType.Integer;
        }

        if (HasOptionalModifier(nativeType, "numeric", IsNumericModifier)
            || HasOptionalModifier(nativeType, "decimal", IsNumericModifier)
            || IsExact(nativeType, "real")
            || IsExact(nativeType, "double precision"))
        {
            return SourceFieldType.Decimal;
        }

        if (IsExact(nativeType, "boolean"))
        {
            return SourceFieldType.Boolean;
        }

        if (IsExact(nativeType, "date")
            || IsTimestamp(nativeType, "without time zone")
            || IsTimestamp(nativeType, "with time zone"))
        {
            return SourceFieldType.Date;
        }

        if (HasOptionalModifier(nativeType, "varchar", IsLengthModifier)
            || HasOptionalModifier(nativeType, "character varying", IsLengthModifier)
            || HasOptionalModifier(nativeType, "char", IsLengthModifier)
            || HasOptionalModifier(nativeType, "character", IsLengthModifier)
            || IsExact(nativeType, "text"))
        {
            return SourceFieldType.String;
        }

        throw new PostgreSqlUnsupportedColumnTypeException(column.Name, column.NativeType);
    }

    private static bool IsTimestamp(string value, string suffix)
    {
        const string baseType = "timestamp";
        if (!value.StartsWith(baseType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = value[baseType.Length..].TrimStart();
        if (remainder.StartsWith('('))
        {
            if (!TryReadModifier(remainder, out var modifier, out remainder)
                || !IsTimestampPrecisionModifier(modifier))
            {
                return false;
            }
        }

        return string.Equals(
            remainder.Trim(),
            suffix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOptionalModifier(
        string value,
        string baseType,
        Func<string, bool> isValidModifier)
    {
        if (!value.StartsWith(baseType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = value[baseType.Length..].TrimStart();
        if (remainder.Length == 0)
        {
            return true;
        }

        return remainder.StartsWith('(')
            && TryReadModifier(remainder, out var modifier, out remainder)
            && remainder.Length == 0
            && isValidModifier(modifier);
    }

    private static bool TryReadModifier(
        string value,
        out string modifier,
        out string remainder)
    {
        modifier = string.Empty;
        remainder = string.Empty;

        if (!value.StartsWith('('))
        {
            return false;
        }

        var closingIndex = value.IndexOf(')');
        if (closingIndex <= 1 || closingIndex != value.LastIndexOf(')'))
        {
            return false;
        }

        modifier = value[1..closingIndex].Trim();
        remainder = value[(closingIndex + 1)..].Trim();
        return modifier.Length > 0;
    }

    private static bool IsNumericModifier(string modifier)
    {
        var values = modifier.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length is < 1 or > 2
            || !TryParseUnsignedInteger(values[0], out var precision)
            || precision is < MinimumNumericPrecision or > MaximumNumericPrecision)
        {
            return false;
        }

        // PostgreSQL permits scales outside the precision, including negative scales.
        return values.Length == 1
            || TryParseSignedInteger(values[1], out var scale)
                && scale is >= MinimumNumericScale and <= MaximumNumericScale;
    }

    private static bool IsLengthModifier(string modifier) =>
        TryParseUnsignedInteger(modifier, out var length)
        && length is >= MinimumCharacterLength and <= MaximumCharacterLength;

    private static bool IsTimestampPrecisionModifier(string modifier) =>
        TryParseUnsignedInteger(modifier, out var precision)
        && precision is >= MinimumTimestampPrecision and <= MaximumTimestampPrecision;

    private static bool TryParseUnsignedInteger(string value, out int result) => int.TryParse(
        value,
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out result);

    private static bool TryParseSignedInteger(string value, out int result) => int.TryParse(
        value,
        NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture,
        out result);

    private static bool IsExact(string value, string expected) => string.Equals(
        value,
        expected,
        StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
