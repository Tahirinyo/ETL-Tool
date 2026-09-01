using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Connections;

public interface ISavedDatabaseConnectionService
{
    Task<SavedDatabaseConnection> CreateAsync(
        string name,
        DatabaseProviderType providerType,
        string connectionConfiguration,
        CancellationToken cancellationToken);

    Task<SavedDatabaseConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SavedDatabaseConnection>> ListAsync(
        DatabaseProviderType? providerType,
        CancellationToken cancellationToken);

    Task<SavedDatabaseConnection?> UpdateAsync(
        Guid id,
        string name,
        string? replacementConfiguration,
        CancellationToken cancellationToken);

    Task<SavedConnectionDeleteResult> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public enum SavedConnectionDeleteResult
{
    Deleted,
    NotFound,
    Referenced
}
