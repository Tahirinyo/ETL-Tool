using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Connections;

public sealed class SavedDatabaseConnectionService :
    ISavedDatabaseConnectionService,
    ISavedConnectionRevisionResolver
{
    private readonly ISavedDatabaseConnectionRepository _repository;
    private readonly ISavedConnectionConfigurationValidator _configurationValidator;
    private readonly ISavedConnectionReferenceChecker _referenceChecker;
    private readonly TimeProvider _timeProvider;

    public SavedDatabaseConnectionService(
        ISavedDatabaseConnectionRepository repository,
        ISavedConnectionConfigurationValidator configurationValidator,
        ISavedConnectionReferenceChecker referenceChecker,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(configurationValidator);
        ArgumentNullException.ThrowIfNull(referenceChecker);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _configurationValidator = configurationValidator;
        _referenceChecker = referenceChecker;
        _timeProvider = timeProvider;
    }

    public async Task<SavedDatabaseConnection> CreateAsync(
        string name,
        DatabaseProviderType providerType,
        string connectionConfiguration,
        CancellationToken cancellationToken)
    {
        var normalizedName = ValidateName(name);
        ValidateProviderType(providerType);
        _configurationValidator.Validate(providerType, connectionConfiguration);

        var now = _timeProvider.GetUtcNow();
        var connection = new SavedDatabaseConnection
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            ProviderType = providerType,
            ActiveRevision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _repository.AddAsync(connection, connectionConfiguration, cancellationToken)
            .ConfigureAwait(false);
        return connection;
    }

    public Task<SavedDatabaseConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        return _repository.GetByIdAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<SavedDatabaseConnection>> ListAsync(
        DatabaseProviderType? providerType,
        CancellationToken cancellationToken)
    {
        if (providerType.HasValue)
        {
            ValidateProviderType(providerType.Value);
        }

        return _repository.ListAsync(providerType, cancellationToken);
    }

    public async Task<SavedDatabaseConnection?> UpdateAsync(
        Guid id,
        string name,
        string? replacementConfiguration,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        var normalizedName = ValidateName(name);
        var existing = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(replacementConfiguration))
        {
            _configurationValidator.Validate(existing.ProviderType, replacementConfiguration);
        }

        existing.Name = normalizedName;
        existing.UpdatedAt = _timeProvider.GetUtcNow();
        return await _repository.UpdateAsync(
                existing,
                string.IsNullOrWhiteSpace(replacementConfiguration) ? null : replacementConfiguration,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SavedConnectionDeleteResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        if (await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return SavedConnectionDeleteResult.NotFound;
        }

        if (await _referenceChecker.IsReferencedAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return SavedConnectionDeleteResult.Referenced;
        }

        return await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
            ? SavedConnectionDeleteResult.Deleted
            : SavedConnectionDeleteResult.NotFound;
    }

    public async Task<SavedConnectionReference> ResolveCurrentAsync(
        Guid connectionId,
        DatabaseProviderType expectedProviderType,
        CancellationToken cancellationToken)
    {
        ValidateId(connectionId);
        ValidateProviderType(expectedProviderType);
        return await _repository.ResolveActiveReferenceAsync(
                connectionId,
                expectedProviderType,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SavedConnectionResolutionException();
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Connection name cannot be empty.", nameof(name));
        }

        return name.Trim();
    }

    private static void ValidateProviderType(DatabaseProviderType providerType)
    {
        if (providerType is not DatabaseProviderType.MongoDb and not DatabaseProviderType.PostgreSql)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerType), providerType, "The database provider type is not supported.");
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
