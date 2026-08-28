using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;

namespace EtlTool.IntegrationTests.Demo;

public sealed class DemoGuideAcceptanceTests
{
    [Theory]
    [InlineData("clean.csv", 20, 0, 5, 0)]
    [InlineData("dirty.csv", 2, 8, 5, 1)]
    public async Task CorrectedDemoConfiguration_ProducesAdvertisedRowOutcomes(
        string fixtureName,
        int expectedValid,
        int expectedInvalid,
        int expectedFiltered,
        int expectedDuplicate)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fixtureName);
        Assert.True(File.Exists(path), $"The demo fixture was not copied to '{path}'.");

        var pipeline = CreatePipeline();
        var processor = CreateProcessor();
        var session = processor.CreateSession(pipeline);
        var results = new List<RowProcessingResult>();

        await using var source = File.OpenRead(path);
        await foreach (var row in new CsvFileExtractor().ReadAsync(
            source,
            pipeline.SourceOptions,
            CancellationToken.None))
        {
            results.Add(session.Process(row));
        }

        Assert.Equal(expectedValid, results.Count(result => result.Status == RowProcessingStatus.Valid));
        Assert.Equal(expectedInvalid, results.Count(result => result.Status == RowProcessingStatus.Invalid));
        Assert.Equal(expectedFiltered, results.Count(result => result.Status == RowProcessingStatus.Filtered));
        Assert.Equal(expectedDuplicate, results.Count(result => result.Status == RowProcessingStatus.Duplicate));

        if (fixtureName == "dirty.csv")
        {
            Assert.Equal(
                [
                    RowProcessingStatus.Valid,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Filtered,
                    RowProcessingStatus.Valid,
                    RowProcessingStatus.Duplicate,
                    RowProcessingStatus.Invalid,
                    RowProcessingStatus.Filtered,
                    RowProcessingStatus.Filtered,
                    RowProcessingStatus.Filtered,
                    RowProcessingStatus.Filtered
                ],
                results.Select(result => result.Status));
            Assert.Equal(
                ["1", "10"],
                results
                    .Where(result => result.Status == RowProcessingStatus.Valid)
                    .Select(result => result.Row.Values["CustomerId"]));
        }
    }

    private static PipelineRowProcessor CreateProcessor() => new(
        new FieldMappingService(),
        new TransformationEngine(new TransformationHandlerRegistry(
        [
            new ConditionalFilterTransformationHandler(),
            new TrimTransformationHandler(),
            new ToLowerTransformationHandler(),
            new ConvertToIntegerTransformationHandler(),
            new ConvertToDecimalTransformationHandler(),
            new ConvertToDateTransformationHandler(),
            new DeduplicateTransformationHandler()
        ])),
        new ValidationEngine(new ValidationHandlerRegistry(
        [
            new RequiredValidationHandler(),
            new EmailValidationHandler(),
            new NumericRangeValidationHandler()
        ])));

    private static PipelineDefinition CreatePipeline() => new()
    {
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            CultureName = "en-US",
            DateFormat = "yyyy-MM-dd",
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema =
        [
            Field("CustomerId"),
            Field("FullName"),
            Field("Email"),
            Field("Age"),
            Field("Balance"),
            Field("BirthDate"),
            Field("Country")
        ],
        FieldMappings =
        [
            Mapping("CustomerId"),
            Mapping("FullName"),
            Mapping("Email"),
            Mapping("Age"),
            Mapping("Balance"),
            Mapping("BirthDate"),
            Mapping("Country")
        ],
        TransformationRules =
        [
            Rule(1, TransformationType.FilterRow, "Country",
                ("Operator", FilterOperator.NotEquals.ToString()), ("Value", "Turkey")),
            Rule(2, TransformationType.Trim, "FullName"),
            Rule(3, TransformationType.ToLower, "Email"),
            Rule(4, TransformationType.ConvertToInteger, "Age"),
            Rule(5, TransformationType.ConvertToDecimal, "Balance"),
            Rule(6, TransformationType.ConvertToDate, "BirthDate"),
            Rule(7, TransformationType.Deduplicate, null, ("Fields", "[\"CustomerId\"]"))
        ],
        ValidationRules =
        [
            Validation(ValidationType.Required, "FullName"),
            Validation(ValidationType.EmailFormat, "Email"),
            Validation(ValidationType.NumericRange, "Age", ("Minimum", "18"), ("Maximum", "100"))
        ],
        DestinationDatabase = "etl_demo",
        DestinationCollection = "customers",
        UpsertKeyField = "CustomerId"
    };

    private static SourceFieldDefinition Field(string name) => new() { Name = name };

    private static FieldMapping Mapping(string name) => new()
    {
        SourceField = name,
        TargetField = name,
        IsIncluded = true
    };

    private static TransformationRule Rule(
        int order,
        TransformationType type,
        string? sourceField,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type,
        SourceField = sourceField,
        Configuration = configuration.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal)
    };

    private static ValidationRule Validation(
        ValidationType type,
        string field,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Field = field,
        Configuration = configuration.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal)
    };
}
