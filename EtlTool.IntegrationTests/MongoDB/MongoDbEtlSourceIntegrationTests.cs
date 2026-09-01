using EtlTool.Application.MongoDB;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoDbEtlSourceIntegrationTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task ReadAsync_UnreachableServerRaisesSafeAccessFailure()
    {
        var options = new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_source_failure_tests",
            SourceExecutionFetchSize = 1
        };
        await using var source = new MongoDbEtlSource(
            new MongoMetadataDatabase(options),
            options,
            new MongoDbSourceOptions
            {
                Database = "reporting",
                Collection = "customers"
            },
            [Field("_id", SourceFieldType.String)]);

        var exception = await Assert.ThrowsAsync<MongoSourceAccessException>(() =>
            ReadAllAsync(source, CancellationToken.None));

        Assert.Equal("The configured MongoDB source could not be accessed.", exception.Message);
        Assert.DoesNotContain("mongodb://", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_StreamsMultipleCursorBatchesInAscendingIdOrder()
    {
        await using var database = fixture.CreateDatabase();
        var sourceDatabaseName = $"{database.DatabaseName}_source";
        var collection = database.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("rows");
        await collection.InsertManyAsync(
        [
            new BsonDocument { ["_id"] = 3, ["value"] = "third" },
            new BsonDocument { ["_id"] = 1, ["value"] = "first" },
            new BsonDocument { ["_id"] = 2, ["value"] = "second" },
            new BsonDocument { ["_id"] = 5, ["value"] = "fifth" },
            new BsonDocument { ["_id"] = 4, ["value"] = "fourth" }
        ]);
        await using var source = CreateSource(
            database,
            sourceDatabaseName,
            "rows",
            fetchSize: 2,
            Field("_id", SourceFieldType.Integer),
            Field("value", SourceFieldType.String));

        var rows = await ReadAllAsync(source, CancellationToken.None);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], rows.Select(row => row.Values["_id"]));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], rows.Select(row => row.SourceRowNumber));
    }

    [Fact]
    public async Task ReadAsync_ConvertsSupportedValuesAndShapesNullAndMissingFields()
    {
        await using var database = fixture.CreateDatabase();
        var sourceDatabaseName = $"{database.DatabaseName}_values";
        var objectId = ObjectId.GenerateNewId();
        var instant = new DateTime(2026, 9, 1, 8, 15, 0, DateTimeKind.Utc);
        await database.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("values")
            .InsertOneAsync(new BsonDocument
            {
                ["_id"] = objectId,
                ["integer"] = 12,
                ["decimal"] = Decimal128.Parse("34.5"),
                ["boolean"] = true,
                ["date"] = new BsonDateTime(instant),
                ["nullable"] = BsonNull.Value
            });
        await using var source = CreateSource(
            database,
            sourceDatabaseName,
            "values",
            fetchSize: 1,
            Field("_id", SourceFieldType.String),
            Field("integer", SourceFieldType.Integer),
            Field("decimal", SourceFieldType.Decimal),
            Field("boolean", SourceFieldType.Boolean),
            Field("date", SourceFieldType.Date),
            Field("nullable", SourceFieldType.String),
            Field("missing", SourceFieldType.String));

        var row = Assert.Single(await ReadAllAsync(source, CancellationToken.None));

        Assert.Equal(objectId.ToString(), row.Values["_id"]);
        Assert.Equal(12L, row.Values["integer"]);
        Assert.Equal(34.5m, row.Values["decimal"]);
        Assert.Equal(true, row.Values["boolean"]);
        Assert.Equal(instant, row.Values["date"]);
        Assert.Null(row.Values["nullable"]);
        Assert.Null(row.Values["missing"]);
    }

    [Fact]
    public async Task ReadAsync_RejectsLossyDoubleDecimalConversionAfterYieldingExactDouble()
    {
        await using var database = fixture.CreateDatabase();
        var sourceDatabaseName = $"{database.DatabaseName}_dbl";
        var collection = database.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("values");
        await collection.InsertManyAsync(
        [
            new BsonDocument { ["_id"] = 1, ["value"] = 0.5d },
            new BsonDocument { ["_id"] = 2, ["value"] = 0.1d }
        ]);
        await using var source = CreateSource(
            database,
            sourceDatabaseName,
            "values",
            fetchSize: 1,
            Field("_id", SourceFieldType.Integer),
            Field("value", SourceFieldType.Decimal));

        await using var enumerator = source.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(0.5m, enumerator.Current.Values["value"]);

        var exception = await Assert.ThrowsAsync<MongoSourceSchemaChangedException>(async () =>
            await enumerator.MoveNextAsync());

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
    }

    [Fact]
    public async Task ReadAsync_EarlyStopDoesNotConvertLaterCursorBatchAndFullReadDetectsDrift()
    {
        await using var database = fixture.CreateDatabase();
        var sourceDatabaseName = $"{database.DatabaseName}_late";
        await database.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("rows")
            .InsertManyAsync(
            [
                new BsonDocument { ["_id"] = 1, ["value"] = "valid" },
                new BsonDocument { ["_id"] = 2, ["value"] = new BsonArray([1]) }
            ]);
        await using var source = CreateSource(
            database,
            sourceDatabaseName,
            "rows",
            fetchSize: 1,
            Field("_id", SourceFieldType.Integer),
            Field("value", SourceFieldType.String));

        await using (var enumerator = source.ReadAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("valid", enumerator.Current.Values["value"]);
        }

        await Assert.ThrowsAsync<MongoSourceSchemaChangedException>(() =>
            ReadAllAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_PropagatesCancellation()
    {
        await using var database = fixture.CreateDatabase();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var source = CreateSource(
            database,
            $"{database.DatabaseName}_cancelled",
            "rows",
            fetchSize: 1,
            Field("_id", SourceFieldType.String));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadAllAsync(source, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static MongoDbEtlSource CreateSource(
        MongoDbTestDatabase database,
        string sourceDatabase,
        string collection,
        int fetchSize,
        params SourceFieldDefinition[] schema)
    {
        var options = new MongoDbOptions
        {
            ConnectionString = database.ConnectionString,
            MetadataDatabaseName = database.DatabaseName,
            SourceExecutionFetchSize = fetchSize
        };
        return new MongoDbEtlSource(
            new MongoMetadataDatabase(options),
            options,
            new MongoDbSourceOptions
            {
                Database = sourceDatabase,
                Collection = collection
            },
            schema);
    }

    private static SourceFieldDefinition Field(string name, SourceFieldType type) => new()
    {
        Name = name,
        DataType = type
    };

    private static async Task<List<EtlTool.Application.Extraction.DataRow>> ReadAllAsync(
        MongoDbEtlSource source,
        CancellationToken cancellationToken)
    {
        var rows = new List<EtlTool.Application.Extraction.DataRow>();
        await foreach (var row in source
            .ReadAsync(cancellationToken)
            .WithCancellation(cancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }
}
