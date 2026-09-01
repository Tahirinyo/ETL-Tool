using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Domain.Entities;

public sealed class PipelineDefinition
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SourceType SourceType { get; set; }

    public SourceOptions SourceOptions { get; set; } = new();

    public PostgreSqlSourceOptions? PostgreSqlSource { get; set; }

    public MongoDbSourceOptions? MongoDbSource { get; set; }

    public List<SourceFieldDefinition> ExpectedSchema { get; set; } = [];

    public List<FieldMapping> FieldMappings { get; set; } = [];

    public bool RequiresRemapping { get; set; }

    public List<TransformationRule> TransformationRules { get; set; } = [];

    public List<ValidationRule> ValidationRules { get; set; } = [];

    public DestinationType DestinationType { get; set; } = DestinationType.MongoDb;

    public PostgreSqlDestinationOptions? PostgreSqlDestination { get; set; }

    public Guid? MongoDbDestinationConnectionId { get; set; }

    public int? MongoDbDestinationConnectionRevision { get; set; }

    public string DestinationDatabase { get; set; } = string.Empty;

    public string DestinationCollection { get; set; } = string.Empty;

    public string UpsertKeyField { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
