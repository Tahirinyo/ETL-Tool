using EtlTool.Application.Connections;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Connections;

public sealed class SavedDatabaseConnectionServiceTests
{
    [Theory]
    [InlineData(DatabaseProviderType.MongoDb)]
    [InlineData(DatabaseProviderType.PostgreSql)]
    public async Task CreateAsync_CreatesIndependentProviderConnection(DatabaseProviderType providerType)
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository);

        var created = await service.CreateAsync(
            " Local database ", providerType, "safe-test-configuration", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Local database", created.Name);
        Assert.Equal(providerType, created.ProviderType);
        Assert.Equal(1, created.ActiveRevision);
        Assert.Equal("safe-test-configuration", repository.LastConfiguration);
    }

    [Fact]
    public async Task UpdateAsync_WithReplacementConfiguration_ActivatesNewRevision()
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository);
        var created = await service.CreateAsync(
            "Database", DatabaseProviderType.MongoDb, "revision-one", CancellationToken.None);

        var admitted = await service.ResolveCurrentAsync(
            created.Id, DatabaseProviderType.MongoDb, CancellationToken.None);
        var updated = await service.UpdateAsync(
            created.Id, "Renamed", "revision-two", CancellationToken.None);
        var future = await service.ResolveCurrentAsync(
            created.Id, DatabaseProviderType.MongoDb, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(2, updated.ActiveRevision);
        Assert.Equal(1, admitted.Revision);
        Assert.Equal(2, future.Revision);
    }

    [Fact]
    public async Task UpdateAsync_WithSuccessiveReplacementConfigurations_AdvancesEachRevision()
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository);
        var created = await service.CreateAsync(
            "Database", DatabaseProviderType.MongoDb, "revision-one", CancellationToken.None);

        await service.UpdateAsync(
            created.Id, "Database", "revision-two", CancellationToken.None);
        var updated = await service.UpdateAsync(
            created.Id, "Database", "revision-three", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(3, updated.ActiveRevision);
    }

    [Fact]
    public async Task UpdateAsync_WithoutReplacement_KeepsActiveRevision()
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository);
        var created = await service.CreateAsync(
            "Database", DatabaseProviderType.PostgreSql, "revision-one", CancellationToken.None);

        var updated = await service.UpdateAsync(created.Id, "Renamed", null, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(1, updated.ActiveRevision);
    }

    [Fact]
    public async Task DeleteAsync_WhenReferenced_IsRejected()
    {
        var repository = new RecordingRepository();
        var checker = new StubReferenceChecker { IsReferenced = true };
        var service = CreateService(repository, checker);
        var created = await service.CreateAsync(
            "Database", DatabaseProviderType.MongoDb, "configuration", CancellationToken.None);

        var result = await service.DeleteAsync(created.Id, CancellationToken.None);

        Assert.Equal(SavedConnectionDeleteResult.Referenced, result);
        Assert.NotNull(await repository.GetByIdAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_WhenUnreferenced_DeletesConnection()
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository);
        var created = await service.CreateAsync(
            "Database", DatabaseProviderType.PostgreSql, "configuration", CancellationToken.None);

        var result = await service.DeleteAsync(created.Id, CancellationToken.None);

        Assert.Equal(SavedConnectionDeleteResult.Deleted, result);
        Assert.Null(await repository.GetByIdAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveCurrentAsync_WithWrongProvider_FailsSafely()
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository);
        var created = await service.CreateAsync(
            "Database", DatabaseProviderType.MongoDb, "configuration", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SavedConnectionResolutionException>(() =>
            service.ResolveCurrentAsync(
                created.Id, DatabaseProviderType.PostgreSql, CancellationToken.None));

        Assert.Equal("The saved database connection is unavailable.", exception.Message);
    }

    [Fact]
    public async Task ListAsync_FiltersByProvider()
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository);
        await service.CreateAsync("Mongo", DatabaseProviderType.MongoDb, "mongo", CancellationToken.None);
        await service.CreateAsync("Postgres", DatabaseProviderType.PostgreSql, "postgres", CancellationToken.None);

        var result = await service.ListAsync(DatabaseProviderType.MongoDb, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(DatabaseProviderType.MongoDb, result[0].ProviderType);
    }

    private static SavedDatabaseConnectionService CreateService(
        RecordingRepository repository,
        StubReferenceChecker? checker = null) => new(
        repository,
        new AcceptingValidator(),
        checker ?? new StubReferenceChecker(),
        TimeProvider.System);

    private sealed class AcceptingValidator : ISavedConnectionConfigurationValidator
    {
        public void Validate(DatabaseProviderType providerType, string connectionConfiguration)
        {
            if (string.IsNullOrWhiteSpace(connectionConfiguration))
            {
                throw new SavedConnectionConfigurationException("Required.");
            }
        }
    }

    private sealed class StubReferenceChecker : ISavedConnectionReferenceChecker
    {
        public bool IsReferenced { get; init; }
        public Task<bool> IsReferencedAsync(Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(IsReferenced);
    }

    private sealed class RecordingRepository : ISavedDatabaseConnectionRepository
    {
        private readonly Dictionary<Guid, SavedDatabaseConnection> _connections = [];
        public string? LastConfiguration { get; private set; }

        public Task AddAsync(SavedDatabaseConnection connection, string connectionConfiguration, CancellationToken cancellationToken)
        {
            _connections.Add(connection.Id, connection);
            LastConfiguration = connectionConfiguration;
            return Task.CompletedTask;
        }

        public Task<SavedDatabaseConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_connections.GetValueOrDefault(id));

        public Task<IReadOnlyList<SavedDatabaseConnection>> ListAsync(DatabaseProviderType? providerType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SavedDatabaseConnection>>(
                _connections.Values.Where(value => !providerType.HasValue || value.ProviderType == providerType).ToList());

        public Task<SavedDatabaseConnection?> UpdateAsync(SavedDatabaseConnection connection, string? replacementConfiguration, CancellationToken cancellationToken)
        {
            if (replacementConfiguration is not null)
            {
                connection.ActiveRevision++;
                LastConfiguration = replacementConfiguration;
            }
            _connections[connection.Id] = connection;
            return Task.FromResult<SavedDatabaseConnection?>(connection);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_connections.Remove(id));

        public Task<SavedConnectionReference?> ResolveActiveReferenceAsync(Guid id, DatabaseProviderType expectedProviderType, CancellationToken cancellationToken)
        {
            var value = _connections.GetValueOrDefault(id);
            return Task.FromResult(value is null || value.ProviderType != expectedProviderType
                ? null
                : new SavedConnectionReference
                {
                    ConnectionId = id,
                    ProviderType = expectedProviderType,
                    Revision = value.ActiveRevision
                });
        }
    }
}
