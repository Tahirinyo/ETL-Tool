using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Connections;
using EtlTool.Infrastructure.MongoDB;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoSavedDatabaseConnectionRepositoryTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task RepositoryReload_PreservesProtectedRevisionsWithoutPersistingPlaintext()
    {
        const string firstSecret = "mongodb://user:distinctive-password-one@localhost:27017";
        const string secondSecret = "mongodb://user:distinctive-password-two@localhost:27017";
        const string thirdSecret = "mongodb://user:distinctive-password-three@localhost:27017";
        await using var database = fixture.CreateDatabase();
        var provider = new EphemeralDataProtectionProvider();
        var firstRepository = Repository(database, provider);
        var connection = Connection(DatabaseProviderType.MongoDb);

        await firstRepository.AddAsync(connection, firstSecret, CancellationToken.None);
        var admittedRevision = await firstRepository.ResolveActiveReferenceAsync(
            connection.Id, DatabaseProviderType.MongoDb, CancellationToken.None);
        var updated = await firstRepository.UpdateAsync(
            connection,
            secondSecret,
            CancellationToken.None);
        var updatedAgain = await firstRepository.UpdateAsync(
            updated!,
            thirdSecret,
            CancellationToken.None);

        var reloadedRepository = Repository(database, provider);
        var reloaded = await reloadedRepository.GetByIdAsync(connection.Id, CancellationToken.None);
        var activeRevision = await reloadedRepository.ResolveActiveReferenceAsync(
            connection.Id, DatabaseProviderType.MongoDb, CancellationToken.None);
        var admittedSecret = await reloadedRepository.ResolveConfigurationAsync(
            admittedRevision!, CancellationToken.None);
        var activeSecret = await reloadedRepository.ResolveConfigurationAsync(
            activeRevision!, CancellationToken.None);
        var raw = await database.Database
            .GetCollection<BsonDocument>(MongoMetadataCollectionNames.SavedDatabaseConnections)
            .Find(new BsonDocument(
                "_id", new BsonBinaryData(connection.Id, GuidRepresentation.Standard)))
            .SingleAsync();
        var rawJson = raw.ToJson();

        Assert.NotNull(updated);
        Assert.NotNull(updatedAgain);
        Assert.NotNull(reloaded);
        Assert.Equal(3, reloaded.ActiveRevision);
        Assert.Equal(1, admittedRevision!.Revision);
        Assert.Equal(3, activeRevision!.Revision);
        Assert.Equal(firstSecret, admittedSecret);
        Assert.Equal(thirdSecret, activeSecret);
        Assert.DoesNotContain("distinctive-password-one", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("distinctive-password-two", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("distinctive-password-three", rawJson, StringComparison.Ordinal);
        Assert.Contains("ProtectedConfiguration", rawJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceChecker_DetectsPipelineAndNonTerminalRunReferences()
    {
        await using var database = fixture.CreateDatabase();
        var metadata = Metadata(database);
        var checker = new MongoSavedConnectionReferenceChecker(metadata);
        var pipelineConnectionId = Guid.NewGuid();
        var runConnectionId = Guid.NewGuid();
        var terminalRunConnectionId = Guid.NewGuid();
        await database.Repository.AddAsync(new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Saved destination",
            MongoDbDestinationConnectionId = pipelineConnectionId
        }, CancellationToken.None);
        await database.EtlRunRepository.AddAsync(new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            PipelineName = "Queued",
            Status = EtlRunStatus.Queued,
            ExecutionConfiguration = new EtlRunExecutionConfiguration
            {
                DestinationConnection = new SavedConnectionReference
                {
                    ConnectionId = runConnectionId,
                    ProviderType = DatabaseProviderType.PostgreSql,
                    Revision = 4
                }
            }
        }, CancellationToken.None);
        await database.EtlRunRepository.AddAsync(new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            PipelineName = "Completed",
            Status = EtlRunStatus.Completed,
            ExecutionConfiguration = new EtlRunExecutionConfiguration
            {
                DestinationConnection = new SavedConnectionReference
                {
                    ConnectionId = terminalRunConnectionId,
                    ProviderType = DatabaseProviderType.PostgreSql,
                    Revision = 2
                }
            }
        }, CancellationToken.None);

        Assert.True(await checker.IsReferencedAsync(pipelineConnectionId, CancellationToken.None));
        Assert.True(await checker.IsReferencedAsync(runConnectionId, CancellationToken.None));
        Assert.False(await checker.IsReferencedAsync(terminalRunConnectionId, CancellationToken.None));
        Assert.False(await checker.IsReferencedAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveConfiguration_TamperedProtectedRevision_FailsSafely()
    {
        const string secret = "Host=localhost;Username=user;Password=distinctive-password";
        await using var database = fixture.CreateDatabase();
        var provider = new EphemeralDataProtectionProvider();
        var repository = Repository(database, provider);
        var connection = Connection(DatabaseProviderType.PostgreSql);
        await repository.AddAsync(connection, secret, CancellationToken.None);
        var rawCollection = database.Database
            .GetCollection<BsonDocument>(MongoMetadataCollectionNames.SavedDatabaseConnections);
        await rawCollection.UpdateOneAsync(
            new BsonDocument(
                "_id", new BsonBinaryData(connection.Id, GuidRepresentation.Standard)),
            new BsonDocument("$set", new BsonDocument("Revisions.0.ProtectedConfiguration", "tampered")));

        var exception = await Assert.ThrowsAsync<EtlTool.Application.Connections.SavedConnectionResolutionException>(() =>
            repository.ResolveConfigurationAsync(new SavedConnectionReference
            {
                ConnectionId = connection.Id,
                ProviderType = connection.ProviderType,
                Revision = 1
            }, CancellationToken.None));

        Assert.Equal("The saved database connection is unavailable.", exception.Message);
        Assert.DoesNotContain("distinctive-password", exception.ToString(), StringComparison.Ordinal);
    }

    private static MongoSavedDatabaseConnectionRepository Repository(
        MongoDbTestDatabase database,
        IDataProtectionProvider provider) => new(
        Metadata(database),
        new DataProtectionConnectionConfigurationProtector(provider));

    private static MongoMetadataDatabase Metadata(MongoDbTestDatabase database) => new(new MongoDbOptions
    {
        ConnectionString = database.ConnectionString,
        MetadataDatabaseName = database.DatabaseName
    });

    private static SavedDatabaseConnection Connection(DatabaseProviderType providerType) => new()
    {
        Id = Guid.NewGuid(),
        Name = providerType == DatabaseProviderType.MongoDb ? "Local Mongo" : "Local PostgreSQL",
        ProviderType = providerType,
        ActiveRevision = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
