using EtlTool.Domain.Enums;

namespace EtlTool.Infrastructure.Connections;

internal sealed class SavedDatabaseConnectionDocument
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DatabaseProviderType ProviderType { get; set; }

    public int ActiveRevision { get; set; }

    public List<ProtectedConnectionRevisionDocument> Revisions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ProtectedConnectionRevisionDocument
{
    public int Revision { get; set; }

    public string ProtectedConfiguration { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
