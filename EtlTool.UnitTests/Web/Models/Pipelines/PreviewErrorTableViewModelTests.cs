using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Models.Pipelines;

namespace EtlTool.UnitTests.Web.Models.Pipelines;

public sealed class PreviewErrorTableViewModelTests
{
    [Fact]
    public void FromPreview_ProjectsEveryValidationErrorInSourceRowAndRuleOrder()
    {
        var pipeline = Pipeline();
        pipeline.ValidationRules =
        [
            Validation(ValidationType.Required, "name", "Name is required."),
            Validation(ValidationType.Required, "name", "Name must be present."),
            Validation(ValidationType.EmailFormat, "email", "Email is invalid.")
        ];
        var session = Processor([], [new RequiredValidationHandler(), new EmailValidationHandler()])
            .CreateSession(pipeline);

        var first = session.Process(Row(2, ("Name", " "), ("Email", "invalid"), ("Amount", null)));
        var second = session.Process(Row(3, ("Name", " "), ("Email", "invalid"), ("Amount", null)));
        var preview = new PreviewResult([first, second]);

        var model = PreviewErrorTableViewModel.FromPreview(preview);

        Assert.Equal(
            [
                (2L, "name", RowProcessingErrorStage.Validation, "Name is required."),
                (2L, "name", RowProcessingErrorStage.Validation, "Name must be present."),
                (2L, "email", RowProcessingErrorStage.Validation, "Email is invalid."),
                (3L, "name", RowProcessingErrorStage.Validation, "Name is required."),
                (3L, "name", RowProcessingErrorStage.Validation, "Name must be present."),
                (3L, "email", RowProcessingErrorStage.Validation, "Email is invalid.")
            ],
            model.Errors.Select(error =>
                (error.SourceRowNumber, error.Field, error.Stage, error.Message)));
    }

    [Fact]
    public void FromPreview_PreservesTransformationFieldContextAndNullRowLevelContext()
    {
        var withField = TransformationFailure("amount", Row(10, ("Name", "Ada"), ("Email", "ada@example.test"), ("Amount", "invalid")));
        var withoutField = TransformationFailure(null, Row(11, ("Name", "Ada"), ("Email", "ada@example.test"), ("Amount", "invalid")));

        var model = PreviewErrorTableViewModel.FromPreview(new PreviewResult([withField, withoutField]));

        Assert.Equal(2, model.Errors.Count);
        Assert.Equal(10, model.Errors[0].SourceRowNumber);
        Assert.Equal("amount", model.Errors[0].Field);
        Assert.Equal(RowProcessingErrorStage.Transformation, model.Errors[0].Stage);
        Assert.Equal("<invalid transformation>", model.Errors[0].Message);
        Assert.Equal(11, model.Errors[1].SourceRowNumber);
        Assert.Null(model.Errors[1].Field);
        Assert.Equal(RowProcessingErrorStage.Transformation, model.Errors[1].Stage);
        Assert.Equal("<invalid transformation>", model.Errors[1].Message);
    }

    [Fact]
    public void FromPreview_RepeatedProjectionPreservesInvalidErrorsAndCounters()
    {
        var invalid = TransformationFailure(
            "amount",
            Row(15, ("Name", "Ada"), ("Email", "ada@example.test"), ("Amount", "invalid")));
        var preview = new PreviewResult([invalid]);

        var first = PreviewErrorTableViewModel.FromPreview(preview);
        var second = PreviewErrorTableViewModel.FromPreview(preview);

        Assert.Equal(
            first.Errors.Select(error =>
                (error.SourceRowNumber, error.Field, error.Stage, error.Message)),
            second.Errors.Select(error =>
                (error.SourceRowNumber, error.Field, error.Stage, error.Message)));
        Assert.Equal(
            [(15L, "amount", RowProcessingErrorStage.Transformation, "<invalid transformation>")],
            first.Errors.Select(error =>
                (error.SourceRowNumber, error.Field, error.Stage, error.Message)));
        Assert.Equal(1, preview.InvalidRowCount);
        var underlyingError = Assert.Single(invalid.Errors);
        Assert.Equal("amount", underlyingError.Field);
        Assert.Equal(RowProcessingErrorStage.Transformation, underlyingError.Stage);
        Assert.Equal("<invalid transformation>", underlyingError.Message);
    }

    [Fact]
    public void FromPreview_ExcludesNonInvalidOutcomesAndDoesNotMutateThePreview()
    {
        var pipeline = Pipeline();
        var valid = Processor([], []).CreateSession(pipeline).Process(
            Row(20, ("Name", "Ada"), ("Email", "ada@example.test"), ("Amount", null)));

        var filteredPipeline = Pipeline();
        filteredPipeline.TransformationRules =
        [
            Transformation(TransformationType.FilterRow, "name",
                ("Operator", FilterOperator.Equals.ToString()), ("Value", "Skip"))
        ];
        var filtered = Processor([new ConditionalFilterTransformationHandler()], [])
            .CreateSession(filteredPipeline)
            .Process(Row(21, ("Name", "Skip"), ("Email", "skip@example.test"), ("Amount", null)));

        var duplicatePipeline = Pipeline();
        duplicatePipeline.TransformationRules = [Transformation(TransformationType.Deduplicate, null, ("Fields", "[\"name\"]"))];
        var duplicateSession = Processor([new DeduplicateTransformationHandler()], [])
            .CreateSession(duplicatePipeline);
        _ = duplicateSession.Process(
            Row(22, ("Name", "Ada"), ("Email", "ada@example.test"), ("Amount", null)));
        var duplicate = duplicateSession.Process(
            Row(23, ("Name", "Ada"), ("Email", "second@example.test"), ("Amount", null)));
        var preview = new PreviewResult([valid, filtered, duplicate]);

        var first = PreviewErrorTableViewModel.FromPreview(preview);
        var second = PreviewErrorTableViewModel.FromPreview(preview);

        Assert.Empty(first.Errors);
        Assert.Empty(second.Errors);
        Assert.Equal(RowProcessingStatus.Valid, valid.Status);
        Assert.Equal(RowProcessingStatus.Filtered, filtered.Status);
        Assert.Equal(RowProcessingStatus.Duplicate, duplicate.Status);
        Assert.All(preview.Rows, row => Assert.Empty(row.Errors));
    }

    private static RowProcessingResult TransformationFailure(string? field, DataRow sourceRow)
    {
        var pipeline = Pipeline();
        pipeline.TransformationRules = [Transformation(TransformationType.Trim, field)];
        return Processor([new FailingTransformationHandler()], [])
            .CreateSession(pipeline)
            .Process(sourceRow);
    }

    private static PipelineRowProcessor Processor(
        IEnumerable<ITransformationHandler> transformations,
        IEnumerable<IValidationHandler> validations) => new(
            new FieldMappingService(),
            new TransformationEngine(new TransformationHandlerRegistry(transformations)),
            new ValidationEngine(new ValidationHandlerRegistry(validations)));

    private static PipelineDefinition Pipeline() => new()
    {
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions { Delimiter = CsvDelimiter.Comma, FirstRowIsHeader = true },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Name" },
            new SourceFieldDefinition { Name = "Email" },
            new SourceFieldDefinition { Name = "Amount" }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Name", TargetField = "name" },
            new FieldMapping { SourceField = "Email", TargetField = "email" },
            new FieldMapping { SourceField = "Amount", TargetField = "amount" }
        ],
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = "name"
    };

    private static ValidationRule Validation(ValidationType type, string field, string message) => new()
    {
        Type = type,
        Field = field,
        ErrorMessage = message
    };

    private static TransformationRule Transformation(
        TransformationType type,
        string? field,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(),
        Order = 1,
        Type = type,
        SourceField = field,
        Configuration = configuration.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
    };

    private static DataRow Row(long sourceRowNumber, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };
        foreach (var (field, value) in values) row.Values.Add(field, value);
        return row;
    }

    private sealed class FailingTransformationHandler : ITransformationHandler
    {
        public TransformationType Type => TransformationType.Trim;

        public TransformationResult Apply(DataRow row, TransformationRule rule) =>
            throw new FormatException("<invalid transformation>");
    }
}
