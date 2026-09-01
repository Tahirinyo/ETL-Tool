using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Connections;

public interface ISavedDatabaseConnectionRepository
{
    Task AddAsync(
        SavedDatabaseConnection connection,
        string connectionConfiguration,
        CancellationToken cancellationToken);

    Task<SavedDatabaseConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SavedDatabaseConnection>> ListAsync(
        DatabaseProviderType? providerType,
        CancellationToken cancellationToken);

    Task<SavedDatabaseConnection?> UpdateAsync(
        SavedDatabaseConnection connection,
        string? replacementConfiguration,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<SavedConnectionReference?> ResolveActiveReferenceAsync(
        Guid id,
        DatabaseProviderType expectedProviderType,
        CancellationToken cancellationToken);
}
