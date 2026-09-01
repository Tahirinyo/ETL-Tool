using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Domain.Entities;

public sealed class EtlRunExecutionConfiguration
{
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

    public string DestinationDatabase { get; set; } = string.Empty;

    public string DestinationCollection { get; set; } = string.Empty;

    public string UpsertKeyField { get; set; } = string.Empty;

    public static EtlRunExecutionConfiguration Capture(PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        return new EtlRunExecutionConfiguration
        {
            SourceType = pipeline.SourceType,
            SourceOptions = Copy(pipeline.SourceOptions),
            PostgreSqlSource = Copy(pipeline.PostgreSqlSource),
            MongoDbSource = Copy(pipeline.MongoDbSource),
            ExpectedSchema = pipeline.ExpectedSchema.Select(Copy).ToList(),
            FieldMappings = pipeline.FieldMappings.Select(Copy).ToList(),
            RequiresRemapping = pipeline.RequiresRemapping,
            TransformationRules = pipeline.TransformationRules.Select(Copy).ToList(),
            ValidationRules = pipeline.ValidationRules.Select(Copy).ToList(),
            DestinationType = pipeline.DestinationType,
            DestinationDatabase = pipeline.DestinationDatabase,
            DestinationCollection = pipeline.DestinationCollection,
            UpsertKeyField = pipeline.UpsertKeyField
        };
    }

    public PipelineDefinition ToPipelineDefinition() => new()
    {
        SourceType = SourceType,
        SourceOptions = Copy(SourceOptions),
        PostgreSqlSource = Copy(PostgreSqlSource),
        MongoDbSource = Copy(MongoDbSource),
        ExpectedSchema = ExpectedSchema.Select(Copy).ToList(),
        FieldMappings = FieldMappings.Select(Copy).ToList(),
        RequiresRemapping = RequiresRemapping,
        TransformationRules = TransformationRules.Select(Copy).ToList(),
        ValidationRules = ValidationRules.Select(Copy).ToList(),
        DestinationType = DestinationType,
        DestinationDatabase = DestinationDatabase,
        DestinationCollection = DestinationCollection,
        UpsertKeyField = UpsertKeyField
    };

    private static SourceOptions Copy(SourceOptions value) => new()
    {
        CultureName = value.CultureName,
        DateFormat = value.DateFormat,
        Delimiter = value.Delimiter,
        WorksheetName = value.WorksheetName,
        FirstRowIsHeader = value.FirstRowIsHeader
    };

    private static PostgreSqlSourceOptions? Copy(PostgreSqlSourceOptions? value) => value is null
        ? null
        : new PostgreSqlSourceOptions
        {
            ConnectionProfile = value.ConnectionProfile,
            Database = value.Database,
            Schema = value.Schema,
            Table = value.Table
        };

    private static MongoDbSourceOptions? Copy(MongoDbSourceOptions? value) => value is null
        ? null
        : new MongoDbSourceOptions
        {
            Database = value.Database,
            Collection = value.Collection
        };

    private static SourceFieldDefinition Copy(SourceFieldDefinition value) => new()
    {
        Name = value.Name,
        DataType = value.DataType
    };

    private static FieldMapping Copy(FieldMapping value) => new()
    {
        SourceField = value.SourceField,
        TargetField = value.TargetField,
        IsIncluded = value.IsIncluded
    };

    private static TransformationRule Copy(TransformationRule value) => new()
    {
        Id = value.Id,
        Type = value.Type,
        Order = value.Order,
        SourceField = value.SourceField,
        Configuration = new Dictionary<string, string>(value.Configuration, StringComparer.Ordinal)
    };

    private static ValidationRule Copy(ValidationRule value) => new()
    {
        Id = value.Id,
        Type = value.Type,
        Field = value.Field,
        Configuration = new Dictionary<string, string>(value.Configuration, StringComparer.Ordinal),
        ErrorMessage = value.ErrorMessage
    };
}
