using EtlTool.Application.MongoDB;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;

namespace EtlTool.UnitTests.Infrastructure.MongoDB;

public sealed class MongoSourceDocumentConverterTests
{
    [Fact]
    public void Convert_ProducesSharedClrValuesInExpectedSchemaOrder()
    {
        var objectId = ObjectId.GenerateNewId();
        var instant = new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Utc);
        var converter = Converter(
            ("text", SourceFieldType.String),
            ("objectId", SourceFieldType.String),
            ("int32", SourceFieldType.Integer),
            ("int64", SourceFieldType.Integer),
            ("double", SourceFieldType.Decimal),
            ("decimal128", SourceFieldType.Decimal),
            ("boolean", SourceFieldType.Boolean),
            ("date", SourceFieldType.Date),
            ("null", SourceFieldType.String),
            ("missing", SourceFieldType.String));
        var document = new BsonDocument
        {
            ["text"] = "Ada",
            ["objectId"] = objectId,
            ["int32"] = 42,
            ["int64"] = 9_000_000_000L,
            ["double"] = 12.5d,
            ["decimal128"] = Decimal128.Parse("23.75"),
            ["boolean"] = true,
            ["date"] = new BsonDateTime(instant),
            ["null"] = BsonNull.Value
        };

        var row = converter.Convert(document, 7, CancellationToken.None);

        Assert.Equal(7, row.SourceRowNumber);
        Assert.Equal(
            ["text", "objectId", "int32", "int64", "double", "decimal128", "boolean", "date", "null", "missing"],
            row.Values.Keys);
        Assert.Equal("Ada", Assert.IsType<string>(row.Values["text"]));
        Assert.Equal(objectId.ToString(), Assert.IsType<string>(row.Values["objectId"]));
        Assert.Equal(42L, Assert.IsType<long>(row.Values["int32"]));
        Assert.Equal(9_000_000_000L, Assert.IsType<long>(row.Values["int64"]));
        Assert.Equal(12.5m, Assert.IsType<decimal>(row.Values["double"]));
        Assert.Equal(23.75m, Assert.IsType<decimal>(row.Values["decimal128"]));
        Assert.True(Assert.IsType<bool>(row.Values["boolean"]));
        var date = Assert.IsType<DateTime>(row.Values["date"]);
        Assert.Equal(instant, date);
        Assert.Equal(DateTimeKind.Utc, date.Kind);
        Assert.Null(row.Values["null"]);
        Assert.Null(row.Values["missing"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2L)]
    [InlineData(9_223_372_036_854_775_807L)]
    public void Convert_AcceptsIntegerBsonForDecimalSchema(object value)
    {
        var document = new BsonDocument("value", BsonValue.Create(value));

        var row = Converter(("value", SourceFieldType.Decimal))
            .Convert(document, 1, CancellationToken.None);

        Assert.Equal(System.Convert.ToDecimal(value), Assert.IsType<decimal>(row.Values["value"]));
    }

    [Theory]
    [MemberData(nameof(ExactlyRepresentableDoubles))]
    public void Convert_AcceptsExactlyRepresentableBsonDoubles(double source, decimal expected)
    {
        var row = Converter(("value", SourceFieldType.Decimal)).Convert(
            new BsonDocument("value", source),
            1,
            CancellationToken.None);

        Assert.Equal(expected, Assert.IsType<decimal>(row.Values["value"]));
    }

    public static TheoryData<double, decimal> ExactlyRepresentableDoubles => new()
    {
        { 0d, 0m },
        { 0.5d, 0.5m },
        { -0.5d, -0.5m },
        { 12.5d, 12.5m },
        { 1d / (1 << 28), 0.0000000037252902984619140625m },
        { 4_503_599_627_370_496d, 4_503_599_627_370_496m }
    };

    [Fact]
    public void Convert_RejectsNonRepresentableNumericValuesWithoutExposingValues()
    {
        var converter = Converter(("value", SourceFieldType.Decimal));
        var tooPreciseDouble = new BsonDocument("value", double.Epsilon);
        var lossyTenthsDouble = new BsonDocument("value", 0.1d);
        var anotherLossyDouble = new BsonDocument("value", 0.2d);
        var tooLargeDouble = new BsonDocument("value", 1e30d);
        var tooLargeDecimal = new BsonDocument(
            "value",
            new BsonDecimal128(Decimal128.Parse("1E+6144")));
        var tooPreciseDecimal = new BsonDocument(
            "value",
            new BsonDecimal128(Decimal128.Parse("0.1234567890123456789012345678901234")));

        var doubleFailure = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            converter.Convert(tooPreciseDouble, 1, CancellationToken.None));
        var tenthsFailure = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            converter.Convert(lossyTenthsDouble, 1, CancellationToken.None));
        var anotherLossyFailure = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            converter.Convert(anotherLossyDouble, 1, CancellationToken.None));
        var overflowFailure = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            converter.Convert(tooLargeDouble, 1, CancellationToken.None));
        var decimalFailure = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            converter.Convert(tooLargeDecimal, 1, CancellationToken.None));
        var precisionFailure = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            converter.Convert(tooPreciseDecimal, 1, CancellationToken.None));

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, doubleFailure.Message);
        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, tenthsFailure.Message);
        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, anotherLossyFailure.Message);
        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, overflowFailure.Message);
        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, decimalFailure.Message);
        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, precisionFailure.Message);
        Assert.DoesNotContain(double.Epsilon.ToString(), doubleFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("0.1", tenthsFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("0.2", anotherLossyFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1E+30", overflowFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1E+6144", decimalFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "0.1234567890123456789012345678901234",
            precisionFailure.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Convert_RejectsNonFiniteBsonDoubles(double value)
    {
        var exception = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            Converter(("value", SourceFieldType.Decimal)).Convert(
                new BsonDocument("value", value),
                1,
                CancellationToken.None));

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
    }

    [Theory]
    [MemberData(nameof(IncompatibleDocuments))]
    public void Convert_RejectsRuntimeSchemaDrift(BsonDocument document)
    {
        var converter = Converter(("value", SourceFieldType.String));

        var exception = Assert.Throws<MongoSourceSchemaChangedException>(() =>
            converter.Convert(document, 1, CancellationToken.None));

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
    }

    [Fact]
    public void Convert_PropagatesCancellationBeforeReturningRow()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            Converter(("value", SourceFieldType.String)).Convert(
                new BsonDocument("value", "secret"),
                1,
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    public static TheoryData<BsonDocument> IncompatibleDocuments => new()
    {
        new BsonDocument { ["value"] = 1 },
        new BsonDocument { ["value"] = new BsonDocument("nested", true) },
        new BsonDocument { ["value"] = new BsonArray([1]) },
        new BsonDocument { ["value"] = new BsonBinaryData([1, 2, 3]) },
        new BsonDocument { ["value"] = "valid", ["unexpected"] = "field" }
    };

    private static MongoSourceDocumentConverter Converter(
        params (string Name, SourceFieldType Type)[] fields) => new(
            fields.Select(field => new SourceFieldDefinition
            {
                Name = field.Name,
                DataType = field.Type
            }).ToArray());
}
