using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Domain;

public sealed class EtlRunExecutionConfigurationTests
{
    [Fact]
    public void Capture_PreservesEveryExecutionSettingAndDeeplyIsolatesMutableState()
    {
        var transformationId = Guid.NewGuid();
        var validationId = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Presentation name",
            Description = "Presentation description",
            SourceType = SourceType.Xlsx,
            SourceOptions = new SourceOptions
            {
                CultureName = "tr-TR",
                DateFormat = "dd.MM.yyyy",
                Delimiter = CsvDelimiter.Tab,
                WorksheetName = "Customers",
                FirstRowIsHeader = false
            },
            PostgreSqlSource = new PostgreSqlSourceOptions
            {
                ConnectionProfile = "ReportingDb",
                Database = "reporting",
                Schema = "public",
                Table = "customers"
            },
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "CustomerId", DataType = SourceFieldType.Integer }
            ],
            FieldMappings =
            [
                new FieldMapping
                {
                    SourceField = "CustomerId",
                    TargetField = "customer_id",
                    IsIncluded = true
                }
            ],
            TransformationRules =
            [
                new TransformationRule
                {
                    Id = transformationId,
                    Type = TransformationType.FindAndReplace,
                    Order = 3,
                    SourceField = "customer_id",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Find"] = "old",
                        ["Replace"] = "new"
                    }
                }
            ],
            ValidationRules =
            [
                new ValidationRule
                {
                    Id = validationId,
                    Type = ValidationType.Required,
                    Field = "customer_id",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Mode"] = "Strict"
                    },
                    ErrorMessage = "Customer identifier is required."
                }
            ],
            DestinationDatabase = "admitted_database",
            DestinationCollection = "admitted_collection",
            UpsertKeyField = "customer_id",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var snapshot = EtlRunExecutionConfiguration.Capture(pipeline);

        pipeline.SourceType = SourceType.Csv;
        pipeline.SourceOptions.CultureName = "en-US";
        pipeline.SourceOptions.DateFormat = "yyyy-MM-dd";
        pipeline.SourceOptions.Delimiter = CsvDelimiter.Comma;
        pipeline.SourceOptions.WorksheetName = "Edited";
        pipeline.SourceOptions.FirstRowIsHeader = true;
        pipeline.PostgreSqlSource.ConnectionProfile = "EditedProfile";
        pipeline.PostgreSqlSource.Database = "edited_database";
        pipeline.PostgreSqlSource.Schema = "edited_schema";
        pipeline.PostgreSqlSource.Table = "edited_table";
        pipeline.ExpectedSchema[0].Name = "EditedId";
        pipeline.FieldMappings[0].TargetField = "edited_id";
        pipeline.TransformationRules[0].Order = 99;
        pipeline.TransformationRules[0].Configuration["Replace"] = "edited";
        pipeline.ValidationRules[0].Field = "edited_id";
        pipeline.ValidationRules[0].Configuration["Mode"] = "Edited";
        pipeline.ValidationRules[0].ErrorMessage = "Edited message";
        pipeline.DestinationDatabase = "edited_database";
        pipeline.DestinationCollection = "edited_collection";
        pipeline.UpsertKeyField = "edited_id";

        Assert.Equal(SourceType.Xlsx, snapshot.SourceType);
        Assert.Equal("tr-TR", snapshot.SourceOptions.CultureName);
        Assert.Equal("dd.MM.yyyy", snapshot.SourceOptions.DateFormat);
        Assert.Equal(CsvDelimiter.Tab, snapshot.SourceOptions.Delimiter);
        Assert.Equal("Customers", snapshot.SourceOptions.WorksheetName);
        Assert.False(snapshot.SourceOptions.FirstRowIsHeader);
        Assert.NotNull(snapshot.PostgreSqlSource);
        Assert.Equal("ReportingDb", snapshot.PostgreSqlSource.ConnectionProfile);
        Assert.Equal("reporting", snapshot.PostgreSqlSource.Database);
        Assert.Equal("public", snapshot.PostgreSqlSource.Schema);
        Assert.Equal("customers", snapshot.PostgreSqlSource.Table);
        Assert.Equal("CustomerId", Assert.Single(snapshot.ExpectedSchema).Name);
        Assert.Equal(SourceFieldType.Integer, snapshot.ExpectedSchema[0].DataType);
        Assert.Equal("customer_id", Assert.Single(snapshot.FieldMappings).TargetField);
        Assert.Equal(transformationId, Assert.Single(snapshot.TransformationRules).Id);
        Assert.Equal(3, snapshot.TransformationRules[0].Order);
        Assert.Equal("new", snapshot.TransformationRules[0].Configuration["Replace"]);
        Assert.Equal(validationId, Assert.Single(snapshot.ValidationRules).Id);
        Assert.Equal("customer_id", snapshot.ValidationRules[0].Field);
        Assert.Equal("Strict", snapshot.ValidationRules[0].Configuration["Mode"]);
        Assert.Equal("Customer identifier is required.", snapshot.ValidationRules[0].ErrorMessage);
        Assert.Equal("admitted_database", snapshot.DestinationDatabase);
        Assert.Equal("admitted_collection", snapshot.DestinationCollection);
        Assert.Equal("customer_id", snapshot.UpsertKeyField);

        Assert.DoesNotContain(
            typeof(EtlRunExecutionConfiguration).GetProperties(),
            property => property.Name is nameof(PipelineDefinition.Name)
                or nameof(PipelineDefinition.Description)
                or nameof(PipelineDefinition.CreatedAt)
                or nameof(PipelineDefinition.UpdatedAt));
    }

    [Fact]
    public void ToPipelineDefinition_ReturnsAnExecutionCopyIndependentFromTheSnapshot()
    {
        var snapshot = EtlRunExecutionConfiguration.Capture(new PipelineDefinition
        {
            SourceOptions = new SourceOptions { CultureName = "tr-TR" },
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
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Value"] = "admitted"
                    }
                }
            ],
            ValidationRules =
            [
                new ValidationRule
                {
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Value"] = "admitted"
                    }
                }
            ]
        });

        var runtime = snapshot.ToPipelineDefinition();
        runtime.SourceOptions.CultureName = "en-US";
        runtime.PostgreSqlSource!.Table = "edited";
        runtime.ExpectedSchema[0].Name = "Edited";
        runtime.FieldMappings[0].TargetField = "edited";
        runtime.TransformationRules[0].Configuration["Value"] = "edited";
        runtime.ValidationRules[0].Configuration["Value"] = "edited";

        Assert.Equal("tr-TR", snapshot.SourceOptions.CultureName);
        Assert.Equal("customers", snapshot.PostgreSqlSource!.Table);
        Assert.Equal("Id", snapshot.ExpectedSchema[0].Name);
        Assert.Equal("id", snapshot.FieldMappings[0].TargetField);
        Assert.Equal("admitted", snapshot.TransformationRules[0].Configuration["Value"]);
        Assert.Equal("admitted", snapshot.ValidationRules[0].Configuration["Value"]);
    }
}
