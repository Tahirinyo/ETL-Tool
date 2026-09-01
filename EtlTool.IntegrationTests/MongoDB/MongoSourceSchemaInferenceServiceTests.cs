using EtlTool.Application.MongoDB;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoSourceSchemaInferenceServiceFailureTests
{
    [Fact]
    public async Task InferAsync_UnreachableServerRaisesSafeAccessFailure()
    {
        var options = new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_metadata"
        };
        var metadataDatabase = new MongoMetadataDatabase(options);
        var discovery = new MongoSourceMetadataDiscoveryService(
            metadataDatabase,
            new MongoTargetAccessService(metadataDatabase, options));
        var service = new MongoSourceSchemaInferenceService(
            metadataDatabase,
            discovery,
            options);

        var exception = await Assert.ThrowsAsync<MongoSourceAccessException>(() =>
            service.InferAsync(
                new MongoDbSourceOptions
                {
                    Database = "reporting",
                    Collection = "customers"
                },
                CancellationToken.None));

        Assert.Equal("The configured MongoDB source could not be accessed.", exception.Message);
        Assert.DoesNotContain("mongodb://", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoSourceSchemaInferenceServiceIntegrationTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task InferAsync_MapsHeterogeneousDocumentsWithNullAndMissingFields()
    {
        await using var metadata = fixture.CreateDatabase();
        var sourceDatabaseName = $"{metadata.DatabaseName}_source";
        var collection = metadata.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("customers");
        await collection.InsertManyAsync(
        [
            new BsonDocument
            {
                ["name"] = "Ali",
                ["age"] = 25,
                ["score"] = 1.5d,
                ["active"] = true,
                ["created"] = new BsonDateTime(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
                ["nullable"] = BsonNull.Value,
                ["nullOnly"] = BsonNull.Value
            },
            new BsonDocument
            {
                ["name"] = "Ayse",
                ["age"] = 26L,
                ["score"] = Decimal128.Parse("2.5"),
                ["phone"] = "555",
                ["nullable"] = "known",
                ["nullOnly"] = BsonNull.Value
            }
        ]);
        var service = CreateService(metadata);

        var schema = await service.InferAsync(
            new MongoDbSourceOptions
            {
                Database = sourceDatabaseName,
                Collection = "customers"
            },
            CancellationToken.None);

        Assert.Equal(
            ["_id", "active", "age", "created", "name", "nullable", "phone", "score"],
            schema.Select(field => field.Name));
        Assert.Equal(
            [
                SourceFieldType.String,
                SourceFieldType.Boolean,
                SourceFieldType.Integer,
                SourceFieldType.Date,
                SourceFieldType.String,
                SourceFieldType.String,
                SourceFieldType.String,
                SourceFieldType.Decimal
            ],
            schema.Select(field => field.DataType));
    }

    [Fact]
    public async Task InferAsync_UsesDeterministicIdOrderingAndDoesNotReadBeyondLimit()
    {
        await using var metadata = fixture.CreateDatabase();
        var sourceDatabaseName = $"{metadata.DatabaseName}_bnd";
        var collection = metadata.Client
            .GetDatabase(sourceDatabaseName)
            .GetCollection<BsonDocument>("events");
        await collection.InsertManyAsync(
        [
            new BsonDocument { ["_id"] = 3, ["unsupportedAfterLimit"] = new BsonArray([1]) },
            new BsonDocument { ["_id"] = 1, ["marker"] = "first" },
            new BsonDocument { ["_id"] = 2, ["marker"] = "second" }
        ]);
        var service = CreateService(metadata, sampleDocumentLimit: 2);
        var source = new MongoDbSourceOptions
        {
            Database = sourceDatabaseName,
            Collection = "events"
        };

        var first = await service.InferAsync(source, CancellationToken.None);
        var second = await service.InferAsync(source, CancellationToken.None);

        Assert.Equal(["_id", "marker"], first.Select(field => field.Name));
        Assert.Equal(
            first.Select(field => (field.Name, field.DataType)),
            second.Select(field => (field.Name, field.DataType)));
    }

    [Fact]
    public async Task InferAsync_RejectsEmptyExistingCollection()
    {
        await using var metadata = fixture.CreateDatabase();
        var sourceDatabaseName = $"{metadata.DatabaseName}_emp";
        await metadata.Client
            .GetDatabase(sourceDatabaseName)
            .CreateCollectionAsync("empty");
        var service = CreateService(metadata);

        var exception = await Assert.ThrowsAsync<MongoSourceSchemaInferenceException>(() =>
            service.InferAsync(
                new MongoDbSourceOptions
                {
                    Database = sourceDatabaseName,
                    Collection = "empty"
                },
                CancellationToken.None));

        Assert.Null(exception.FieldName);
        Assert.Empty(exception.ObservedTypes);
    }

    [Fact]
    public async Task InferAsync_RejectsMissingCollectionWithSafeNotFoundFailure()
    {
        await using var metadata = fixture.CreateDatabase();
        var sourceDatabaseName = $"{metadata.DatabaseName}_mis";
        await metadata.Client
            .GetDatabase(sourceDatabaseName)
            .CreateCollectionAsync("existing");
        var service = CreateService(metadata);

        var exception = await Assert.ThrowsAsync<MongoSourceMetadataObjectNotFoundException>(() =>
            service.InferAsync(
                new MongoDbSourceOptions
                {
                    Database = sourceDatabaseName,
                    Collection = "missing"
                },
                CancellationToken.None));

        Assert.Equal("collection", exception.ObjectType);
        Assert.DoesNotContain(sourceDatabaseName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferAsync_PropagatesCancellation()
    {
        await using var metadata = fixture.CreateDatabase();
        var service = CreateService(metadata);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.InferAsync(
                new MongoDbSourceOptions
                {
                    Database = "reporting",
                    Collection = "customers"
                },
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static MongoSourceSchemaInferenceService CreateService(
        MongoDbTestDatabase database,
        int sampleDocumentLimit = 100)
    {
        var options = new MongoDbOptions
        {
            ConnectionString = database.ConnectionString,
            MetadataDatabaseName = database.DatabaseName,
            SourceSchemaSampleDocumentLimit = sampleDocumentLimit
        };
        var metadataDatabase = new MongoMetadataDatabase(options);
        var discovery = new MongoSourceMetadataDiscoveryService(
            metadataDatabase,
            new MongoTargetAccessService(metadataDatabase, options));
        return new MongoSourceSchemaInferenceService(
            metadataDatabase,
            discovery,
            options);
    }
}
