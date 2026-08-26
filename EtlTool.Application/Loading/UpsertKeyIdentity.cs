using EtlTool.Application.Extraction;

namespace EtlTool.Application.Loading;

public sealed class UpsertKeyIdentity : IEquatable<UpsertKeyIdentity>
{
    private static readonly DateTime UnixEpoch =
        new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly KeyKind _kind;
    private readonly object _comparisonValue;

    private UpsertKeyIdentity(
        KeyKind kind,
        object comparisonValue,
        object effectiveValue)
    {
        _kind = kind;
        _comparisonValue = comparisonValue;
        EffectiveValue = effectiveValue;
    }

    public object EffectiveValue { get; }

    public static UpsertKeyIdentity Create(DataRow row, string field)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (!row.Values.TryGetValue(field, out var value))
        {
            throw new InvalidOperationException(
                $"Upsert key field '{field}' is missing from row {row.SourceRowNumber}.");
        }

        return Create(value, field, row.SourceRowNumber);
    }

    public static UpsertKeyIdentity Create(
        object? value,
        string field,
        long sourceRowNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (value is null || value is string { Length: 0 } || value is string text && string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Upsert key field '{field}' is empty in row {sourceRowNumber}.");
        }

        try
        {
            return value switch
            {
                string stringValue => new UpsertKeyIdentity(KeyKind.String, stringValue, stringValue),
                bool boolValue => new UpsertKeyIdentity(KeyKind.Boolean, boolValue, boolValue),
                sbyte numeric => Numeric(numeric),
                byte numeric => Numeric(numeric),
                short numeric => Numeric(numeric),
                ushort numeric => Numeric(numeric),
                int numeric => Numeric(numeric),
                uint numeric => Numeric(numeric),
                long numeric => Numeric(numeric),
                ulong numeric => Numeric(numeric),
                decimal numeric => Numeric(numeric),
                float numeric when float.IsFinite(numeric) => Numeric(Convert.ToDecimal(numeric)),
                double numeric when double.IsFinite(numeric) => Numeric(Convert.ToDecimal(numeric)),
                DateTime dateTime => Date(Normalize(dateTime)),
                DateTimeOffset dateTimeOffset => Date(dateTimeOffset.UtcDateTime),
                _ => throw new InvalidOperationException(
                    $"Upsert key field '{field}' in row {sourceRowNumber} has unsupported value type '{value.GetType().FullName}'.")
            };
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"Upsert key field '{field}' in row {sourceRowNumber} cannot be represented as a MongoDB numeric identity.",
                exception);
        }
    }

    public bool Equals(UpsertKeyIdentity? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || _kind != other._kind) return false;

        return _kind == KeyKind.String
            ? StringComparer.Ordinal.Equals((string)_comparisonValue, (string)other._comparisonValue)
            : _comparisonValue.Equals(other._comparisonValue);
    }

    public override bool Equals(object? obj) => Equals(obj as UpsertKeyIdentity);

    public override int GetHashCode() => _kind == KeyKind.String
        ? HashCode.Combine(_kind, StringComparer.Ordinal.GetHashCode((string)_comparisonValue))
        : HashCode.Combine(_kind, _comparisonValue);

    private static UpsertKeyIdentity Numeric(decimal value) =>
        new(KeyKind.Numeric, value, value);

    private static UpsertKeyIdentity Date(DateTime value)
    {
        var millisecondsSinceEpoch =
            (value - UnixEpoch).Ticks / TimeSpan.TicksPerMillisecond;
        var effectiveValue = UnixEpoch.AddTicks(
            millisecondsSinceEpoch * TimeSpan.TicksPerMillisecond);

        return new UpsertKeyIdentity(
            KeyKind.Date,
            millisecondsSinceEpoch,
            effectiveValue);
    }

    private static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private enum KeyKind
    {
        String,
        Boolean,
        Numeric,
        Date
    }
}
