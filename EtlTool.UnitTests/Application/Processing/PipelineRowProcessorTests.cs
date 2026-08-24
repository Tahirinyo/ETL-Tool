using System.Text.Json;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Processing;

public sealed class PipelineRowProcessorTests
{
    [Fact]
    public void Process_MapsThenAppliesPersistedTransformationOrderBeforeValidation()
    {
        var pipeline = Pipeline(Mapping("Raw", "value"));
        pipeline.TransformationRules =
        [
            Rule(20, TransformationType.SetDefaultValue, "value", ("Value", "Unknown")),
            Rule(10, TransformationType.Trim, "value")
        ];
        pipeline.ValidationRules = [Validation(ValidationType.Required, "value")];
        var session = Processor(
                [new TrimTransformationHandler(), new DefaultValueTransformationHandler()],
                [new RequiredValidationHandler()])
            .CreateSession(pipeline);
        var source = Row(2, ("Raw", "   "));

        var result = session.Process(source);

        Assert.Equal(RowProcessingStatus.Valid, result.Status);
        Assert.Equal("Unknown", result.Row.Values["value"]);
        Assert.Equal("   ", source.Values["Raw"]);
    }

    [Fact]
    public void Process_UsesPipelineCultureForTransformationAndValidation()
    {
        var pipeline = Pipeline(Mapping("Amount", "amount"));
        pipeline.SourceOptions.CultureName = "tr-TR";
        pipeline.TransformationRules = [Rule(1, TransformationType.ConvertToDecimal, "amount")];
        pipeline.ValidationRules =
        [
            Validation(
                ValidationType.NumericRange,
                "amount",
                ("Minimum", "1,5"),
                ("Maximum", "1,5"))
        ];
        var session = Processor(
                [new ConvertToDecimalTransformationHandler()],
                [new NumericRangeValidationHandler()])
            .CreateSession(pipeline);

        var result = session.Process(Row(2, ("Amount", "1,5")));

        Assert.Equal(RowProcessingStatus.Valid, result.Status);
        Assert.Equal(1.5m, Assert.IsType<decimal>(result.Row.Values["amount"]));
    }

    [Fact]
    public void Process_PreservesFilteredAndDuplicateOutcomesAndSkipsValidation()
    {
        var validation = new TrackingValidationHandler();
        var filterPipeline = Pipeline(Mapping("Kind", "kind"), Mapping("Id", "id"));
        filterPipeline.TransformationRules =
        [
            Rule(
                1,
                TransformationType.FilterRow,
                "kind",
                ("Operator", FilterOperator.Equals.ToString()),
                ("Value", "skip"))
        ];
        filterPipeline.ValidationRules = [Validation(ValidationType.Required, "id")];
        var filteredSession = Processor(
                [new ConditionalFilterTransformationHandler()],
                [validation])
            .CreateSession(filterPipeline);

        var filtered = filteredSession.Process(Row(2, ("Kind", "skip"), ("Id", "A")));

        Assert.Equal(RowProcessingStatus.Filtered, filtered.Status);
        Assert.Empty(filtered.Errors);
        Assert.Equal(0, validation.InvocationCount);

        var duplicatePipeline = Pipeline(Mapping("Id", "id"));
        duplicatePipeline.TransformationRules = [DeduplicateRule(1, "id")];
        duplicatePipeline.ValidationRules = [Validation(ValidationType.Required, "id")];
        var duplicateSession = Processor(
                [new DeduplicateTransformationHandler()],
                [validation])
            .CreateSession(duplicatePipeline);

        var first = duplicateSession.Process(Row(2, ("Id", "A")));
        var duplicate = duplicateSession.Process(Row(3, ("Id", "A")));

        Assert.Equal(RowProcessingStatus.Valid, first.Status);
        Assert.Equal(RowProcessingStatus.Duplicate, duplicate.Status);
        Assert.Empty(duplicate.Errors);
        Assert.Equal(1, validation.InvocationCount);
    }

    [Fact]
    public void Process_ConvertsTransformationFailureToInvalidAndContinuesWithoutCommittingDeduplication()
    {
        var conversionRule = Rule(2, TransformationType.ConvertToInteger, "value");
        var pipeline = Pipeline(Mapping("Id", "id"), Mapping("Value", "value"));
        pipeline.TransformationRules = [DeduplicateRule(1, "id"), conversionRule];
        var session = Processor(
                [new DeduplicateTransformationHandler(), new ConvertToIntegerTransformationHandler()],
                [])
            .CreateSession(pipeline);

        var failed = session.Process(Row(2, ("Id", "A"), ("Value", "not-a-number")));
        var recovered = session.Process(Row(3, ("Id", "A"), ("Value", "1")));
        var duplicate = session.Process(Row(4, ("Id", "A"), ("Value", "2")));

        Assert.Equal(RowProcessingStatus.Invalid, failed.Status);
        var error = Assert.Single(failed.Errors);
        Assert.Equal(RowProcessingErrorStage.Transformation, error.Stage);
        Assert.Equal("value", error.Field);
        Assert.Equal(conversionRule.Id, error.RuleId);
        Assert.Equal(TransformationType.ConvertToInteger, error.TransformationType);
        Assert.Equal(RowProcessingStatus.Valid, recovered.Status);
        Assert.Equal(RowProcessingStatus.Duplicate, duplicate.Status);
    }

    [Fact]
    public void Process_PreservesAllValidationErrors()
    {
        var pipeline = Pipeline(Mapping("Name", "name"), Mapping("Email", "email"));
        pipeline.ValidationRules =
        [
            Validation(ValidationType.Required, "name"),
            Validation(ValidationType.EmailFormat, "email")
        ];
        var session = Processor(
                [],
                [new RequiredValidationHandler(), new EmailValidationHandler()])
            .CreateSession(pipeline);

        var result = session.Process(Row(2, ("Name", " "), ("Email", "invalid")));

        Assert.Equal(RowProcessingStatus.Invalid, result.Status);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, error =>
            Assert.Equal(RowProcessingErrorStage.Validation, error.Stage));
        Assert.Equal(["name", "email"], result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void Process_PropagatesMappingAndUnexpectedTransformationFailures()
    {
        var mappingSession = Processor([], []).CreateSession(Pipeline(Mapping("Raw", "value")));
        Assert.Throws<InvalidOperationException>(() =>
            mappingSession.Process(Row(2, ("Different", "value"))));

        var pipeline = Pipeline(Mapping("Raw", "value"));
        pipeline.TransformationRules = [Rule(1, TransformationType.Trim, "value")];
        var failureSession = Processor([new UnexpectedFailureHandler()], []).CreateSession(pipeline);

        var exception = Assert.Throws<ApplicationException>(() =>
            failureSession.Process(Row(2, ("Raw", "value"))));
        Assert.Equal("Unexpected failure.", exception.Message);
    }

    private static PipelineRowProcessor Processor(
        IEnumerable<ITransformationHandler> transformationHandlers,
        IEnumerable<IValidationHandler> validationHandlers) => new(
            new FieldMappingService(),
            new TransformationEngine(new TransformationHandlerRegistry(transformationHandlers)),
            new ValidationEngine(new ValidationHandlerRegistry(validationHandlers)));

    private static PipelineDefinition Pipeline(params FieldMapping[] mappings) => new()
    {
        Id = Guid.NewGuid(),
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema = mappings
            .Select(mapping => new SourceFieldDefinition { Name = mapping.SourceField })
            .ToList(),
        FieldMappings = mappings.ToList(),
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = mappings[0].TargetField
    };

    private static FieldMapping Mapping(string source, string target) => new()
    {
        SourceField = source,
        TargetField = target,
        IsIncluded = true
    };

    private static TransformationRule Rule(
        int order,
        TransformationType type,
        string? field,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type,
        SourceField = field,
        Configuration = configuration.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal)
    };

    private static TransformationRule DeduplicateRule(int order, params string[] fields) =>
        Rule(
            order,
            TransformationType.Deduplicate,
            null,
            ("Fields", JsonSerializer.Serialize(fields)));

    private static ValidationRule Validation(
        ValidationType type,
        string field,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Field = field,
        Configuration = configuration.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal)
    };

    private static DataRow Row(
        long sourceRowNumber,
        params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };
        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }

    private sealed class TrackingValidationHandler : IValidationHandler
    {
        public ValidationType Type => ValidationType.Required;

        public int InvocationCount { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule)
        {
            InvocationCount++;
            return ValidationResult.Valid(row);
        }
    }

    private sealed class UnexpectedFailureHandler : ITransformationHandler
    {
        public TransformationType Type => TransformationType.Trim;

        public TransformationResult Apply(DataRow row, TransformationRule rule) =>
            throw new ApplicationException("Unexpected failure.");
    }
}
