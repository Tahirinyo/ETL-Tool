using EtlTool.Domain.Enums;

namespace EtlTool.Domain.Entities;

public sealed class SavedDatabaseConnection
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DatabaseProviderType ProviderType { get; set; }

    public int ActiveRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
