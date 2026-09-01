using EtlTool.Domain.Enums;

namespace EtlTool.Web.Models.Pipelines;

public sealed class PipelineListItemViewModel
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public SourceType SourceType { get; init; }

    public DestinationType DestinationType { get; init; }

    public string DestinationDatabase { get; init; } = string.Empty;

    public string DestinationCollection { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public string SourceTypeDisplay => SourceType == SourceType.Unspecified
        ? "Not configured"
        : SourceType.ToString();

    public string DestinationDatabaseDisplay => string.IsNullOrWhiteSpace(DestinationDatabase)
        ? "Not configured"
        : DestinationDatabase;

    public string DestinationCollectionDisplay => string.IsNullOrWhiteSpace(DestinationCollection)
        ? "Not configured"
        : DestinationCollection;

    public string DestinationTypeDisplay => DestinationType == DestinationType.Unspecified
        ? "Not configured"
        : DestinationType == DestinationType.PostgreSql ? "PostgreSQL" : "MongoDB";
}
