using EtlTool.Application.MongoDB;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;

namespace EtlTool.UnitTests.Infrastructure.MongoDB;

public sealed class MongoSourceSchemaAccumulatorTests
{
    [Fact]
    public void Build_MapsSupportedScalarTypesAndOrdersFieldsOrdinally()
    {
        var accumulator = new MongoSourceSchemaAccumulator();

        accumulator.Observe(new BsonDocument
        {
            ["text"] = "value",
            ["objectId"] = ObjectId.GenerateNewId(),
            ["int32"] = 1,
            ["int64"] = 2L,
            ["double"] = 1.5d,
            ["decimal128"] = Decimal128.Parse("2.5"),
            ["boolean"] = true,
            ["date"] = new BsonDateTime(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))
        }, CancellationToken.None);

        var schema = accumulator.Build();

        Assert.Equal(
            ["boolean", "date", "decimal128", "double", "int32", "int64", "objectId", "text"],
            schema.Select(field => field.Name));
        Assert.Equal(
            [
                SourceFieldType.Boolean,
                SourceFieldType.Date,
                SourceFieldType.Decimal,
                SourceFieldType.Decimal,
                SourceFieldType.Integer,
                SourceFieldType.Integer,
                SourceFieldType.String,
                SourceFieldType.String
            ],
            schema.Select(field => field.DataType));
    }

    [Fact]
    public void Build_IgnoresMissingAndNullEvidenceAndWidensCompatibleNumbers()
    {
        var accumulator = new MongoSourceSchemaAccumulator();
        accumulator.Observe(new BsonDocument
        {
            ["sometimesMissing"] = 1,
            ["nullable"] = BsonNull.Value,
            ["nullOnly"] = BsonNull.Value,
            ["number"] = 1
        }, CancellationToken.None);
        accumulator.Observe(new BsonDocument
        {
            ["nullable"] = "known",
            ["nullOnly"] = BsonNull.Value,
            ["number"] = 1.25d
        }, CancellationToken.None);

        var schema = accumulator.Build();

        Assert.Equal(["nullable", "number", "sometimesMissing"], schema.Select(field => field.Name));
        Assert.Equal(
            [SourceFieldType.String, SourceFieldType.Decimal, SourceFieldType.Integer],
            schema.Select(field => field.DataType));
    }

    [Fact]
    public void Observe_RejectsEmptyFieldNameAndDoesNotReturnPartialSchema()
    {
        var accumulator = new MongoSourceSchemaAccumulator();
        accumulator.Observe(new BsonDocument("valid", 1), CancellationToken.None);

        var exception = Assert.Throws<MongoSourceSchemaInferenceException>(() =>
            accumulator.Observe(new BsonDocument(string.Empty, "sensitive value"), CancellationToken.None));

        Assert.Null(exception.FieldName);
        Assert.Empty(exception.ObservedTypes);
        Assert.Contains("empty name", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_RejectsWhitespaceOnlyFieldName()
    {
        var accumulator = new MongoSourceSchemaAccumulator();

        var exception = Assert.Throws<MongoSourceSchemaInferenceException>(() =>
            accumulator.Observe(new BsonDocument(" \t", 1), CancellationToken.None));

        Assert.Null(exception.FieldName);
        Assert.Empty(exception.ObservedTypes);
        Assert.Contains("empty name", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(IncompatibleValues))]
    public void Observe_RejectsIncompatibleConcreteTypes(
        BsonValue first,
        BsonValue second,
        string firstType,
        string secondType)
    {
        var accumulator = new MongoSourceSchemaAccumulator();
        accumulator.Observe(new BsonDocument("value", first), CancellationToken.None);

        var exception = Assert.Throws<MongoSourceSchemaInferenceException>(() =>
            accumulator.Observe(new BsonDocument("value", second), CancellationToken.None));

        Assert.Equal("value", exception.FieldName);
        Assert.Equal([firstType, secondType], exception.ObservedTypes);
        Assert.DoesNotContain(first.ToString()!, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(second.ToString()!, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsupportedValues))]
    public void Observe_RejectsUnsupportedBsonWithoutIncludingValues(
        BsonValue value,
        string expectedType)
    {
        var accumulator = new MongoSourceSchemaAccumulator();

        var exception = Assert.Throws<MongoSourceSchemaInferenceException>(() =>
            accumulator.Observe(new BsonDocument("unsupported", value), CancellationToken.None));

        Assert.Equal("unsupported", exception.FieldName);
        Assert.Equal([expectedType], exception.ObservedTypes);
        Assert.Contains(expectedType, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value.ToString()!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsEmptyAndNullOnlyEvidence()
    {
        var empty = new MongoSourceSchemaAccumulator();
        var nullOnly = new MongoSourceSchemaAccumulator();
        nullOnly.Observe(
            new BsonDocument("nullable", BsonNull.Value),
            CancellationToken.None);

        var emptyException = Assert.Throws<MongoSourceSchemaInferenceException>(empty.Build);
        var nullOnlyException = Assert.Throws<MongoSourceSchemaInferenceException>(nullOnly.Build);

        Assert.Null(emptyException.FieldName);
        Assert.Empty(emptyException.ObservedTypes);
        Assert.Equal(emptyException.Message, nullOnlyException.Message);
    }

    [Fact]
    public void Observe_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            new MongoSourceSchemaAccumulator().Observe(
                new BsonDocument("value", 1),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    public static TheoryData<BsonValue, BsonValue, string, string> IncompatibleValues => new()
    {
        { new BsonInt32(1), new BsonString("text"), "Integer", "String" },
        { new BsonDateTime(0), new BsonString("date"), "Date", "String" },
        { new BsonBoolean(true), new BsonInt32(1), "Boolean", "Integer" },
        { new BsonObjectId(ObjectId.GenerateNewId()), new BsonString("id"), "ObjectId", "String" },
        { new BsonObjectId(ObjectId.GenerateNewId()), new BsonInt64(1), "ObjectId", "Integer" }
    };

    public static TheoryData<BsonValue, string> UnsupportedValues => new()
    {
        { new BsonDocument("nested", 1), "Document" },
        { new BsonArray([1, 2]), "Array" },
        { new BsonBinaryData([1, 2, 3]), "Binary" },
        { BsonMinKey.Value, "MinKey" }
    };
}
