using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Connections;

public interface ISavedConnectionRevisionResolver
{
    Task<SavedConnectionReference> ResolveCurrentAsync(
        Guid connectionId,
        DatabaseProviderType expectedProviderType,
        CancellationToken cancellationToken);
}
