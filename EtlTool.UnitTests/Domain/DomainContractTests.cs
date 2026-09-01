using System.Globalization;
using System.Text.Json;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Domain;

public sealed class DomainContractTests
{
    [Fact]
    public void EnumContracts_ExposeApprovedNamesAndStableValues()
    {
        Assert.Equal(
            ["Unspecified:0", "Csv:1", "Xlsx:2", "PostgreSql:3", "MongoDb:4"],
            GetEnumContract<SourceType>());
        Assert.Equal(
            ["Comma:1", "Semicolon:2", "Tab:3"],
            GetEnumContract<CsvDelimiter>());
        Assert.Equal(
            ["Unknown:0", "String:1", "Integer:2", "Decimal:3", "Date:4", "Boolean:5"],
            GetEnumContract<SourceFieldType>());
        Assert.Equal(
            [
                "Unspecified:0",
                "Equals:1",
                "NotEquals:2",
                "GreaterThan:3",
                "GreaterThanOrEqual:4",
                "LessThan:5",
                "LessThanOrEqual:6"
            ],
            GetEnumContract<FilterOperator>());
        Assert.Equal(
            [
                "Unspecified:0",
                "Trim:1",
                "ToUpper:2",
                "ToLower:3",
                "ConvertToString:4",
                "ConvertToInteger:5",
                "ConvertToDecimal:6",
                "ConvertToDate:7",
                "SetDefaultValue:8",
                "FilterRow:9",
                "FindAndReplace:10",
                "Deduplicate:11"
            ],
            GetEnumContract<TransformationType>());
        Assert.Equal(
            [
                "Unspecified:0",
                "Required:1",
                "EmailFormat:2",
                "NumericRange:3",
                "TextLengthRange:4",
                "DateRange:5",
                "UpsertKeyRequired:6"
            ],
            GetEnumContract<ValidationType>());
        Assert.Equal(
            [
                "Queued:0",
                "Running:1",
                "Completed:2",
                "PartiallyCompleted:3",
                "Failed:4",
                "Interrupted:5"
            ],
            GetEnumContract<EtlRunStatus>());
    }

    [Fact]
    public void PipelineDefinition_InitializesIndependentEditableState()
    {
        var first = new PipelineDefinition();
        var second = new PipelineDefinition();

        Assert.Null(first.Description);
        Assert.Empty(first.ExpectedSchema);
        Assert.Empty(first.FieldMappings);
        Assert.Empty(first.TransformationRules);
        Assert.Empty(first.ValidationRules);
        Assert.True(first.SourceOptions.FirstRowIsHeader);
        Assert.Null(first.PostgreSqlSource);
        Assert.Null(first.MongoDbSource);

        Assert.NotSame(first.SourceOptions, second.SourceOptions);
        Assert.NotSame(first.ExpectedSchema, second.ExpectedSchema);
        Assert.NotSame(first.FieldMappings, second.FieldMappings);
        Assert.NotSame(first.TransformationRules, second.TransformationRules);
        Assert.NotSame(first.ValidationRules, second.ValidationRules);
    }

    [Fact]
    public void PipelineDefinition_DoesNotContainMongoCredentialMaterial()
    {
        Assert.DoesNotContain(
            typeof(PipelineDefinition).GetProperties(),
            property => property.Name.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PostgreSqlSourceOptions_ContainsOnlySourceIdentityMetadata()
    {
        Assert.Equal(
            ["SavedConnectionId", "SavedConnectionRevision", "ConnectionProfile", "Database", "Schema", "Table"],
            typeof(PostgreSqlSourceOptions).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void MongoDbSourceOptions_ContainsOnlySourceIdentityMetadata()
    {
        Assert.Equal(
            ["SavedConnectionId", "SavedConnectionRevision", "Database", "Collection"],
            typeof(MongoDbSourceOptions).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void PostgreSqlSourceMetadata_SerializesWithoutCredentialOrConnectionStringFields()
    {
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.PostgreSql,
            PostgreSqlSource = new PostgreSqlSourceOptions
            {
                ConnectionProfile = "ReportingDb",
                Database = "reporting",
                Schema = "public",
                Table = "customers"
            }
        };

        var pipelineJson = JsonSerializer.Serialize(pipeline);
        var snapshotJson = JsonSerializer.Serialize(EtlRunExecutionConfiguration.Capture(pipeline));

        Assert.Contains("ConnectionProfile", pipelineJson, StringComparison.Ordinal);
        Assert.Contains("ConnectionProfile", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", pipelineJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", pipelineJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", pipelineJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", snapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public void MongoDbSourceMetadata_SerializesWithoutCredentialOrConnectionStringFields()
    {
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.MongoDb,
            MongoDbSource = new MongoDbSourceOptions
            {
                Database = "reporting",
                Collection = "customers"
            }
        };

        var pipelineJson = JsonSerializer.Serialize(pipeline);
        var snapshotJson = JsonSerializer.Serialize(EtlRunExecutionConfiguration.Capture(pipeline));

        Assert.Contains("MongoDbSource", pipelineJson, StringComparison.Ordinal);
        Assert.Contains("MongoDbSource", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", pipelineJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", pipelineJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", pipelineJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", snapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFieldType_BooleanAppendsWithoutChangingPersistedEnumValues()
    {
        var legacy = new SourceFieldDefinition
        {
            Name = "LegacyAmount",
            DataType = SourceFieldType.Decimal
        };
        var boolean = new SourceFieldDefinition
        {
            Name = "IsActive",
            DataType = SourceFieldType.Boolean
        };

        Assert.Equal(3, (int)legacy.DataType);
        Assert.Equal(5, (int)boolean.DataType);
        Assert.Contains("\"DataType\":3", JsonSerializer.Serialize(legacy), StringComparison.Ordinal);
        Assert.Contains("\"DataType\":5", JsonSerializer.Serialize(boolean), StringComparison.Ordinal);
        Assert.Equal(
            SourceFieldType.Boolean,
            JsonSerializer.Deserialize<SourceFieldDefinition>(JsonSerializer.Serialize(boolean))!.DataType);
    }

    [Fact]
    public void SourceOptions_ResolveCultureUsesInvariantForOmittedNullAndEmptyNames()
    {
        var omitted = new SourceOptions();
        var explicitNull = new SourceOptions { CultureName = null! };
        var empty = new SourceOptions { CultureName = string.Empty };

        Assert.Same(CultureInfo.InvariantCulture, omitted.ResolveCulture());
        Assert.Same(CultureInfo.InvariantCulture, explicitNull.ResolveCulture());
        Assert.Same(CultureInfo.InvariantCulture, empty.ResolveCulture());
    }

    [Theory]
    [InlineData("tr-TR", "tr-TR")]
    [InlineData("TR-tr", "tr-TR")]
    [InlineData("en-US", "en-US")]
    public void SourceOptions_ResolveCultureReturnsCanonicalInstalledSpecificCulture(
        string cultureName,
        string expectedName)
    {
        var options = new SourceOptions { CultureName = cultureName };

        var culture = options.ResolveCulture();

        Assert.Equal(expectedName, culture.Name);
        Assert.False(culture.IsNeutralCulture);
        Assert.Equal(cultureName, options.CultureName);
    }

    [Fact]
    public void SourceOptions_ResolveCultureDoesNotChangeCurrentCultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var options = new SourceOptions { CultureName = "tr-TR" };

        _ = options.ResolveCulture();

        Assert.Equal(originalCulture, CultureInfo.CurrentCulture);
        Assert.Equal(originalUiCulture, CultureInfo.CurrentUICulture);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("tr")]
    [InlineData("xx-XX")]
    [InlineData("not-a-real-culture")]
    public void SourceOptions_ResolveCultureRejectsUnsupportedNames(string cultureName)
    {
        var options = new SourceOptions { CultureName = cultureName };

        var exception = Assert.Throws<CultureNotFoundException>(options.ResolveCulture);

        Assert.Equal(cultureName, exception.InvalidCultureName);
    }

    [Fact]
    public void RuleConfigurations_AreIndependentAndUseOrdinalKeys()
    {
        var firstTransformation = new TransformationRule();
        var secondTransformation = new TransformationRule();
        var firstValidation = new ValidationRule();
        var secondValidation = new ValidationRule();

        firstTransformation.Configuration["Value"] = "first";
        firstTransformation.Configuration["value"] = "second";
        firstValidation.Configuration["Value"] = "first";
        firstValidation.Configuration["value"] = "second";

        Assert.Equal(2, firstTransformation.Configuration.Count);
        Assert.Equal(2, firstValidation.Configuration.Count);
        Assert.Equal(StringComparer.Ordinal, firstTransformation.Configuration.Comparer);
        Assert.Equal(StringComparer.Ordinal, firstValidation.Configuration.Comparer);
        Assert.NotSame(firstTransformation.Configuration, secondTransformation.Configuration);
        Assert.NotSame(firstValidation.Configuration, secondValidation.Configuration);
        Assert.Empty(secondValidation.Configuration);
    }

    [Fact]
    public void EtlRun_DefaultsToQueuedWithOpenLifecycleAndNoErrors()
    {
        var run = new EtlRun();

        Assert.Equal(EtlRunStatus.Queued, run.Status);
        Assert.Null(run.StartedAt);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.SystemError);
        Assert.Null(run.ErrorReportPath);
    }

    private static string[] GetEnumContract<TEnum>()
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()
            .Select(value => $"{value}:{Convert.ToInt32(value)}")
            .ToArray();
    }
}
