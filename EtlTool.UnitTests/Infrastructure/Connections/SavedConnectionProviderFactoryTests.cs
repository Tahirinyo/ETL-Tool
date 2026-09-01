using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Connections;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.PostgreSql;

namespace EtlTool.UnitTests.Infrastructure.Connections;

public sealed class SavedConnectionProviderFactoryTests
{
    [Fact]
    public async Task CreateProviderContext_WithMissingRevision_FailsSafelyBeforeResolution()
    {
        var resolver = new RecordingRuntimeResolver();
        var factory = CreateFactory(resolver);

        var exception = await Assert.ThrowsAsync<EtlTool.Application.Connections.SavedConnectionResolutionException>(
            () => factory.CreateMongoDbAsync(Guid.NewGuid(), 0, CancellationToken.None));

        Assert.Equal("The saved database connection is unavailable.", exception.Message);
        Assert.Empty(resolver.References);
    }

    [Fact]
    public async Task CreateProviderContexts_ResolveExactAdmittedRevision()
    {
        var resolver = new RecordingRuntimeResolver();
        var factory = CreateFactory(resolver);
        var mongoId = Guid.NewGuid();
        resolver.Configuration = "mongodb://saved:27017";

        var mongo = await factory.CreateMongoDbAsync(mongoId, 3, CancellationToken.None);
        var postgresId = Guid.NewGuid();
        resolver.Configuration = "Host=saved;Database=etl;Username=user;Password=password";
        var postgres = await factory.CreatePostgreSqlAsync(postgresId, 8, CancellationToken.None);

        Assert.NotNull(mongo.MetadataDatabase);
        Assert.NotNull(postgres.ConnectionFactory);
        Assert.Collection(
            resolver.References,
            reference =>
            {
                Assert.Equal(mongoId, reference.ConnectionId);
                Assert.Equal(3, reference.Revision);
                Assert.Equal(DatabaseProviderType.MongoDb, reference.ProviderType);
            },
            reference =>
            {
                Assert.Equal(postgresId, reference.ConnectionId);
                Assert.Equal(8, reference.Revision);
                Assert.Equal(DatabaseProviderType.PostgreSql, reference.ProviderType);
            });
    }

    private static SavedConnectionProviderFactory CreateFactory(
        ISavedConnectionRuntimeResolver resolver) => new(
            resolver,
            new MongoDbOptions
            {
                ConnectionString = "mongodb://legacy:27017",
                MetadataDatabaseName = "etl_tool_metadata"
            },
            new PostgreSqlConnectionOptions());

    private sealed class RecordingRuntimeResolver : ISavedConnectionRuntimeResolver
    {
        public string Configuration { get; set; } = string.Empty;
        public List<SavedConnectionReference> References { get; } = [];

        public Task<string> ResolveConfigurationAsync(
            SavedConnectionReference reference,
            CancellationToken cancellationToken)
        {
            References.Add(reference);
            return Task.FromResult(Configuration);
        }
    }
}
