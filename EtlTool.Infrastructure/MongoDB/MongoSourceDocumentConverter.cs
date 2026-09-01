using EtlTool.Application.Extraction;
using EtlTool.Application.MongoDB;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using MongoDB.Bson;
using System.Numerics;

namespace EtlTool.Infrastructure.MongoDB;

internal sealed class MongoSourceDocumentConverter
{
    private static readonly BigInteger DecimalMaximumCoefficient =
        (BigInteger.One << 96) - BigInteger.One;

    private readonly SourceFieldDefinition[] _expectedSchema;
    private readonly HashSet<string> _expectedFieldNames;

    public MongoSourceDocumentConverter(IReadOnlyList<SourceFieldDefinition> expectedSchema)
    {
        ArgumentNullException.ThrowIfNull(expectedSchema);

        _expectedSchema = expectedSchema
            .Select(field => new SourceFieldDefinition
            {
                Name = field?.Name ?? string.Empty,
                DataType = field?.DataType ?? SourceFieldType.Unknown
            })
            .ToArray();
        _expectedFieldNames = new HashSet<string>(StringComparer.Ordinal);

        if (_expectedSchema.Length == 0
            || _expectedSchema.Any(field => string.IsNullOrWhiteSpace(field.Name))
            || _expectedSchema.Any(field => !_expectedFieldNames.Add(field.Name)))
        {
            throw new ArgumentException(
                "The MongoDB execution source requires a valid expected schema.",
                nameof(expectedSchema));
        }
    }

    public DataRow Convert(
        BsonDocument document,
        long sourceRowNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var element in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_expectedFieldNames.Contains(element.Name))
            {
                throw new MongoSourceSchemaChangedException();
            }
        }

        var row = new DataRow { SourceRowNumber = sourceRowNumber };
        foreach (var field in _expectedSchema)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row.Values.Add(
                field.Name,
                document.TryGetValue(field.Name, out var value)
                    ? ConvertValue(value, field.DataType)
                    : null);
        }

        return row;
    }

    private static object? ConvertValue(BsonValue value, SourceFieldType expectedType)
    {
        if (value is BsonNull)
        {
            return null;
        }

        try
        {
            return expectedType switch
            {
                SourceFieldType.String when value is BsonString text => text.Value,
                SourceFieldType.String when value is BsonObjectId objectId => objectId.Value.ToString(),
                SourceFieldType.Integer when value is BsonInt32 integer => (long)integer.Value,
                SourceFieldType.Integer when value is BsonInt64 integer => integer.Value,
                SourceFieldType.Decimal when value is BsonInt32 integer => (decimal)integer.Value,
                SourceFieldType.Decimal when value is BsonInt64 integer => (decimal)integer.Value,
                SourceFieldType.Decimal when value is BsonDouble number => ConvertDouble(number.Value),
                SourceFieldType.Decimal when value is BsonDecimal128 number =>
                    ConvertDecimal128(number.Value),
                SourceFieldType.Boolean when value is BsonBoolean boolean => boolean.Value,
                SourceFieldType.Date when value is BsonDateTime dateTime => dateTime.ToUniversalTime(),
                _ => throw new MongoSourceSchemaChangedException()
            };
        }
        catch (MongoSourceSchemaChangedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OverflowException or FormatException)
        {
            throw new MongoSourceSchemaChangedException();
        }
    }

    private static decimal ConvertDouble(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new MongoSourceSchemaChangedException();
        }

        if (value == 0d)
        {
            return decimal.Zero;
        }

        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        var isNegative = (bits & (1UL << 63)) != 0;
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        var fractionBits = bits & ((1UL << 52) - 1);
        var significand = exponentBits == 0
            ? fractionBits
            : fractionBits | (1UL << 52);
        var binaryExponent = exponentBits == 0
            ? -1074
            : exponentBits - 1075;

        int scale;
        BigInteger coefficient;
        if (binaryExponent >= 0)
        {
            scale = 0;
            coefficient = new BigInteger(significand) << binaryExponent;
        }
        else
        {
            scale = -binaryExponent;
            var removableTwos = Math.Min(scale, BitOperations.TrailingZeroCount(significand));
            significand >>= removableTwos;
            scale -= removableTwos;

            if (scale > 28)
            {
                throw new MongoSourceSchemaChangedException();
            }

            coefficient = new BigInteger(significand) * BigInteger.Pow(5, scale);
        }

        if (coefficient > DecimalMaximumCoefficient)
        {
            throw new MongoSourceSchemaChangedException();
        }

        return new decimal(
            (int)(uint)(coefficient & uint.MaxValue),
            (int)(uint)((coefficient >> 32) & uint.MaxValue),
            (int)(uint)((coefficient >> 64) & uint.MaxValue),
            isNegative,
            (byte)scale);
    }

    private static decimal ConvertDecimal128(Decimal128 value)
    {
        decimal converted;
        try
        {
            converted = Decimal128.ToDecimal(value);
        }
        catch (Exception exception) when (exception is OverflowException or FormatException)
        {
            throw new MongoSourceSchemaChangedException();
        }

        return Decimal128.Compare(value, new Decimal128(converted)) == 0
            ? converted
            : throw new MongoSourceSchemaChangedException();
    }
}
