using EtlTool.Application.Connections;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.Connections;

public interface ISavedConnectionRuntimeResolver
{
    Task<string> ResolveConfigurationAsync(
        SavedConnectionReference reference,
        CancellationToken cancellationToken);
}

public sealed class MongoSavedDatabaseConnectionRepository :
    ISavedDatabaseConnectionRepository,
    ISavedConnectionRuntimeResolver
{
    private readonly IMongoCollection<SavedDatabaseConnectionDocument> _collection;
    private readonly IConnectionConfigurationProtector _protector;

    public MongoSavedDatabaseConnectionRepository(
        MongoMetadataDatabase metadataDatabase,
        IConnectionConfigurationProtector protector)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        ArgumentNullException.ThrowIfNull(protector);
        _collection = metadataDatabase.SavedDatabaseConnections;
        _protector = protector;
    }

    public async Task AddAsync(
        SavedDatabaseConnection connection,
        string connectionConfiguration,
        CancellationToken cancellationToken)
    {
        ValidateConnection(connection);
        if (connection.ActiveRevision != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(connection));
        }

        var document = new SavedDatabaseConnectionDocument
        {
            Id = connection.Id,
            Name = connection.Name,
            ProviderType = connection.ProviderType,
            ActiveRevision = 1,
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
            Revisions =
            [
                new ProtectedConnectionRevisionDocument
                {
                    Revision = 1,
                    ProtectedConfiguration = _protector.Protect(connectionConfiguration),
                    CreatedAt = connection.CreatedAt
                }
            ]
        };

        try
        {
            await _collection.InsertOneAsync(document, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InvalidOperationException("A saved connection with this identifier already exists.", exception);
        }
    }

    public async Task<SavedDatabaseConnection?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        var document = await _collection.Find(value => value.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToDomain(document);
    }

    public async Task<IReadOnlyList<SavedDatabaseConnection>> ListAsync(
        DatabaseProviderType? providerType,
        CancellationToken cancellationToken)
    {
        var filter = providerType.HasValue
            ? Builders<SavedDatabaseConnectionDocument>.Filter.Eq(
                value => value.ProviderType, providerType.Value)
            : Builders<SavedDatabaseConnectionDocument>.Filter.Empty;
        var documents = await _collection.Find(filter)
            .SortBy(value => value.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(ToDomain).ToArray();
    }

    public async Task<SavedDatabaseConnection?> UpdateAsync(
        SavedDatabaseConnection connection,
        string? replacementConfiguration,
        CancellationToken cancellationToken)
    {
        ValidateConnection(connection);
        var existing = await _collection.Find(value => value.Id == connection.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (existing.ProviderType != connection.ProviderType)
        {
            throw new InvalidOperationException("The saved connection provider type cannot be changed.");
        }

        var nextRevision = existing.ActiveRevision;
        var update = Builders<SavedDatabaseConnectionDocument>.Update
            .Set(value => value.Name, connection.Name)
            .Set(value => value.UpdatedAt, connection.UpdatedAt);
        if (replacementConfiguration is not null)
        {
            nextRevision++;
            update = update
                .Set(value => value.ActiveRevision, nextRevision)
                .Push(value => value.Revisions, new ProtectedConnectionRevisionDocument
                {
                    Revision = nextRevision,
                    ProtectedConfiguration = _protector.Protect(replacementConfiguration),
                    CreatedAt = connection.UpdatedAt
                });
        }

        var result = await _collection.UpdateOneAsync(
                Builders<SavedDatabaseConnectionDocument>.Filter.And(
                    Builders<SavedDatabaseConnectionDocument>.Filter.Eq(value => value.Id, connection.Id),
                    Builders<SavedDatabaseConnectionDocument>.Filter.Eq(
                        value => value.ActiveRevision, existing.ActiveRevision)),
                update,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.MatchedCount == 0)
        {
            throw new InvalidOperationException("The saved connection changed during the update.");
        }

        connection.ActiveRevision = nextRevision;
        return connection;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        var result = await _collection.DeleteOneAsync(value => value.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return result.DeletedCount > 0;
    }

    public async Task<SavedConnectionReference?> ResolveActiveReferenceAsync(
        Guid id,
        DatabaseProviderType expectedProviderType,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        var document = await _collection.Find(value => value.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (document is null
            || document.ProviderType != expectedProviderType
            || document.ActiveRevision < 1
            || document.Revisions is null
            || !document.Revisions.Any(value =>
                value.Revision == document.ActiveRevision
                && !string.IsNullOrWhiteSpace(value.ProtectedConfiguration)))
        {
            return null;
        }

        return new SavedConnectionReference
        {
            ConnectionId = document.Id,
            ProviderType = document.ProviderType,
            Revision = document.ActiveRevision
        };
    }

    public async Task<string> ResolveConfigurationAsync(
        SavedConnectionReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.ConnectionId == Guid.Empty || reference.Revision < 1
            || reference.ProviderType is DatabaseProviderType.Unspecified
            || !Enum.IsDefined(reference.ProviderType))
        {
            throw new SavedConnectionResolutionException();
        }

        var document = await _collection.Find(value => value.Id == reference.ConnectionId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var revision = document?.Revisions?.FirstOrDefault(
            value => value.Revision == reference.Revision);
        if (document is null || document.ProviderType != reference.ProviderType || revision is null)
        {
            throw new SavedConnectionResolutionException();
        }

        return _protector.Unprotect(revision.ProtectedConfiguration);
    }

    private static SavedDatabaseConnection ToDomain(SavedDatabaseConnectionDocument document) => new()
    {
        Id = document.Id,
        Name = document.Name,
        ProviderType = document.ProviderType,
        ActiveRevision = document.ActiveRevision,
        CreatedAt = document.CreatedAt,
        UpdatedAt = document.UpdatedAt
    };

    private static void ValidateConnection(SavedDatabaseConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateId(connection.Id);
        if (string.IsNullOrWhiteSpace(connection.Name))
        {
            throw new ArgumentException("Connection name cannot be empty.", nameof(connection));
        }
        if (connection.ProviderType is not DatabaseProviderType.MongoDb
            and not DatabaseProviderType.PostgreSql)
        {
            throw new ArgumentOutOfRangeException(nameof(connection));
        }
        if (connection.ActiveRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(connection));
        }
    }

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Connection identifier cannot be empty.", nameof(id));
        }
    }
}
