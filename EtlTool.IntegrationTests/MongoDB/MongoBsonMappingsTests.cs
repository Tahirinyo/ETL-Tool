using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoBsonMappingsTests
{
    [Fact]
    public void EtlRunMapping_SerializesRunAndPipelineIdentifiersAsStandardUuids()
    {
        _ = new MongoMetadataDatabase(new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_bson_mapping_tests"
        });
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            PipelineName = "Customer import",
            OriginalFileName = "customers.csv",
            StoredFilePath = "runs/source.csv"
        };

        var document = run.ToBsonDocument();

        var runId = Assert.IsType<BsonBinaryData>(document["_id"]);
        var pipelineId = Assert.IsType<BsonBinaryData>(document[nameof(EtlRun.PipelineId)]);
        Assert.Equal(BsonBinarySubType.UuidStandard, runId.SubType);
        Assert.Equal(BsonBinarySubType.UuidStandard, pipelineId.SubType);
    }

    [Fact]
    public void EtlRunMapping_RoundTripsNestedExecutionConfigurationAndAllowsLegacyDocuments()
    {
        _ = new MongoMetadataDatabase(new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_bson_mapping_tests"
        });
        var transformationId = Guid.NewGuid();
        var validationId = Guid.NewGuid();
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(new PipelineDefinition
            {
                SourceType = SourceType.Csv,
                SourceOptions = new SourceOptions
                {
                    CultureName = "tr-TR",
                    DateFormat = "dd.MM.yyyy",
                    Delimiter = CsvDelimiter.Semicolon
                },
                PostgreSqlSource = new PostgreSqlSourceOptions
                {
                    ConnectionProfile = "ReportingDb",
                    Database = "reporting",
                    Schema = "public",
                    Table = "customers"
                },
                ExpectedSchema = [new SourceFieldDefinition { Name = "Id" }],
                FieldMappings = [new FieldMapping { SourceField = "Id", TargetField = "id" }],
                TransformationRules =
                [
                    new TransformationRule
                    {
                        Id = transformationId,
                        Type = TransformationType.Trim,
                        Order = 1,
                        SourceField = "id",
                        Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Mode"] = "Both"
                        }
                    }
                ],
                ValidationRules =
                [
                    new ValidationRule
                    {
                        Id = validationId,
                        Type = ValidationType.Required,
                        Field = "id",
                        ErrorMessage = "Id is required."
                    }
                ],
                DestinationDatabase = "etl_target",
                DestinationCollection = "customers",
                UpsertKeyField = "id"
            })
        };

        var document = run.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<EtlRun>(document);
        var legacyDocument = new BsonDocument(document);
        legacyDocument.Remove(nameof(EtlRun.ExecutionConfiguration));
        var legacy = BsonSerializer.Deserialize<EtlRun>(legacyDocument);

        Assert.NotNull(roundTripped.ExecutionConfiguration);
        Assert.Equal(
            run.ExecutionConfiguration!.ToBsonDocument(),
            roundTripped.ExecutionConfiguration.ToBsonDocument());
        Assert.Equal(
            BsonBinarySubType.UuidStandard,
            document[nameof(EtlRun.ExecutionConfiguration)]
                .AsBsonDocument[nameof(EtlRunExecutionConfiguration.TransformationRules)]
                .AsBsonArray[0]
                .AsBsonDocument["_id"]
                .AsBsonBinaryData.SubType);
        Assert.Equal(transformationId, roundTripped.ExecutionConfiguration.TransformationRules[0].Id);
        Assert.Equal(validationId, roundTripped.ExecutionConfiguration.ValidationRules[0].Id);
        Assert.NotNull(roundTripped.ExecutionConfiguration.PostgreSqlSource);
        Assert.Equal("ReportingDb", roundTripped.ExecutionConfiguration.PostgreSqlSource.ConnectionProfile);
        Assert.DoesNotContain("ConnectionString", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(legacy.ExecutionConfiguration);
    }

    [Fact]
    public void PipelineMapping_RoundTripsExistingAndPostgreSqlSchemaFieldTypes()
    {
        _ = new MongoMetadataDatabase(new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_bson_mapping_tests"
        });
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.PostgreSql,
            PostgreSqlSource = new PostgreSqlSourceOptions
            {
                ConnectionProfile = "ReportingDb",
                Database = "reporting",
                Schema = "public",
                Table = "customers"
            },
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "LegacyAmount", DataType = SourceFieldType.Decimal },
                new SourceFieldDefinition { Name = "IsActive", DataType = SourceFieldType.Boolean }
            ]
        };

        var document = pipeline.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<PipelineDefinition>(document);
        var fields = document[nameof(PipelineDefinition.ExpectedSchema)].AsBsonArray;

        Assert.Equal(3, fields[0].AsBsonDocument[nameof(SourceFieldDefinition.DataType)].AsInt32);
        Assert.Equal(5, fields[1].AsBsonDocument[nameof(SourceFieldDefinition.DataType)].AsInt32);
        Assert.Equal(
            pipeline.ExpectedSchema.Select(field => (field.Name, field.DataType)),
            roundTripped.ExpectedSchema.Select(field => (field.Name, field.DataType)));
    }

    [Fact]
    public void PipelineMapping_RoundTripsMongoDbSourceIdentityAndInferredSharedSchema()
    {
        _ = new MongoMetadataDatabase(new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_bson_mapping_tests"
        });
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.MongoDb,
            MongoDbSource = new MongoDbSourceOptions
            {
                Database = "reporting",
                Collection = "customers"
            },
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "_id", DataType = SourceFieldType.String },
                new SourceFieldDefinition { Name = "Age", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Amount", DataType = SourceFieldType.Decimal },
                new SourceFieldDefinition { Name = "Active", DataType = SourceFieldType.Boolean },
                new SourceFieldDefinition { Name = "Created", DataType = SourceFieldType.Date }
            ]
        };

        var document = pipeline.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<PipelineDefinition>(document);
        var execution = BsonSerializer.Deserialize<EtlRunExecutionConfiguration>(
            EtlRunExecutionConfiguration.Capture(pipeline).ToBsonDocument());

        Assert.Equal(SourceType.MongoDb, roundTripped.SourceType);
        Assert.Equal("reporting", roundTripped.MongoDbSource!.Database);
        Assert.Equal("customers", roundTripped.MongoDbSource.Collection);
        Assert.Equal(
            pipeline.ExpectedSchema.Select(field => (field.Name, field.DataType)),
            roundTripped.ExpectedSchema.Select(field => (field.Name, field.DataType)));
        Assert.Equal(
            pipeline.ExpectedSchema.Select(field => (field.Name, field.DataType)),
            execution.ExpectedSchema.Select(field => (field.Name, field.DataType)));
        Assert.NotNull(execution.MongoDbSource);
        Assert.DoesNotContain("ConnectionString", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", document.ToJson(), StringComparison.OrdinalIgnoreCase);
    }
}
