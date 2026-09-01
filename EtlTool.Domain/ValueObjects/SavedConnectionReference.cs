using EtlTool.Domain.Enums;

namespace EtlTool.Domain.ValueObjects;

public sealed class SavedConnectionReference
{
    public Guid ConnectionId { get; set; }

    public DatabaseProviderType ProviderType { get; set; }

    public int Revision { get; set; }
}
