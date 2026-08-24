using System.Text.Json;
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

public sealed class PreviewTableViewModelTests
{
    [Fact]
    public void FromPreview_UsesMappedOutputColumnsInPersistedOrderAndDisplaysTransformedValues()
    {
        var pipeline = Pipeline();
        var processor = Processor([new TrimTransformationHandler()], []);
        pipeline.TransformationRules = [Rule(1, TransformationType.Trim, "displayName")];
        var source = Row(2, ("Name", " Ada "), ("Amount", 42L), ("When", new DateTime(2026, 8, 24)));
        var result = processor.CreateSession(pipeline).Process(source);

        var model = PreviewTableViewModel.FromPreview(new PreviewResult([result]), pipeline);

        Assert.Equal(["displayName", "total", "createdAt"], model.Columns);
        var row = Assert.Single(model.Rows);
        Assert.Equal(2, row.SourceRowNumber);
        Assert.Equal(["Ada", "42", "08/24/2026 00:00:00"], row.Cells.Select(cell => cell.DisplayValue));
        Assert.Equal(" Ada ", source.Values["Name"]);
    }

    [Fact]
    public void FromPreview_IncludesOnlyValidAndValidationInvalidRows()
    {
        var pipeline = Pipeline();
        var valid = Processor([], []).CreateSession(pipeline).Process(
            Row(2, ("Name", "Valid"), ("Amount", 1L), ("When", null)));

        var validationPipeline = Pipeline();
        validationPipeline.ValidationRules = [new ValidationRule { Type = ValidationType.Required, Field = "displayName" }];
        var validationInvalid = Processor([], [new RequiredValidationHandler()])
            .CreateSession(validationPipeline)
            .Process(Row(3, ("Name", " "), ("Amount", 2L), ("When", null)));

        var filteredPipeline = Pipeline();
        filteredPipeline.TransformationRules =
        [
            Rule(1, TransformationType.FilterRow, "displayName",
                ("Operator", FilterOperator.Equals.ToString()), ("Value", "Filtered"))
        ];
        var filtered = Processor([new ConditionalFilterTransformationHandler()], [])
            .CreateSession(filteredPipeline)
            .Process(Row(4, ("Name", "Filtered"), ("Amount", 3L), ("When", null)));

        var duplicatePipeline = Pipeline();
        duplicatePipeline.TransformationRules = [DeduplicateRule(1, "displayName")];
        var duplicateSession = Processor([new DeduplicateTransformationHandler()], [])
            .CreateSession(duplicatePipeline);
        _ = duplicateSession.Process(Row(5, ("Name", "Duplicate"), ("Amount", 4L), ("When", null)));
        var duplicate = duplicateSession.Process(Row(6, ("Name", "Duplicate"), ("Amount", 5L), ("When", null)));

        var failedPipeline = Pipeline();
        failedPipeline.TransformationRules = [Rule(1, TransformationType.ConvertToInteger, "total")];
        var transformationInvalid = Processor([new ConvertToIntegerTransformationHandler()], [])
            .CreateSession(failedPipeline)
            .Process(Row(7, ("Name", "Failure"), ("Amount", "not-a-number"), ("When", null)));

        var model = PreviewTableViewModel.FromPreview(
            new PreviewResult([valid, validationInvalid, filtered, duplicate, transformationInvalid]),
            pipeline);

        Assert.Equal([2L, 3L], model.Rows.Select(row => row.SourceRowNumber));
    }

    [Fact]
    public void FromPreview_DisplaysTransformedValuesForValidationInvalidRowsWithoutChangingPreviewCounters()
    {
        var pipeline = Pipeline();
        pipeline.TransformationRules = [Rule(1, TransformationType.Trim, "displayName")];
        pipeline.ValidationRules = [new ValidationRule { Type = ValidationType.Required, Field = "total" }];
        var result = Processor([new TrimTransformationHandler()], [new RequiredValidationHandler()])
            .CreateSession(pipeline)
            .Process(Row(10, ("Name", " Ada "), ("Amount", " "), ("When", null)));
        var preview = new PreviewResult([result]);

        var first = PreviewTableViewModel.FromPreview(preview, pipeline);
        var second = PreviewTableViewModel.FromPreview(preview, pipeline);

        Assert.Equal(RowProcessingStatus.Invalid, result.Status);
        Assert.Equal(
            ["Ada", "Whitespace-only value (length: 1)", "Null value"],
            Assert.Single(first.Rows).Cells.Select(cell => cell.DisplayValue));
        Assert.Equal(first.Columns, second.Columns);
        Assert.Equal(first.Rows.Select(row => row.SourceRowNumber), second.Rows.Select(row => row.SourceRowNumber));
        Assert.Equal(
            first.Rows.SelectMany(row => row.Cells).Select(cell => cell.DisplayValue),
            second.Rows.SelectMany(row => row.Cells).Select(cell => cell.DisplayValue));
        Assert.Equal(0, preview.ValidRowCount);
        Assert.Equal(1, preview.InvalidRowCount);
        Assert.Equal(0, preview.FilteredRowCount);
        Assert.Equal("Ada", result.Row.Values["displayName"]);
    }

    [Fact]
    public void FromPreview_DistinguishesMissingNullEmptyAndWhitespaceValuesWithoutMutatingRows()
    {
        var pipeline = Pipeline();
        var result = Processor([], []).CreateSession(pipeline).Process(
            Row(9, ("Name", null), ("Amount", ""), ("When", null)));
        result.Row.Values.Remove("createdAt");
        result.Row.Values["total"] = "  ";

        var model = PreviewTableViewModel.FromPreview(new PreviewResult([result]), pipeline);

        Assert.Equal(
            ["Null value", "Whitespace-only value (length: 2)", "Unavailable"],
            Assert.Single(model.Rows).Cells.Select(cell => cell.DisplayValue));
        Assert.Null(result.Row.Values["displayName"]);
        Assert.Equal("  ", result.Row.Values["total"]);
        Assert.False(result.Row.Values.ContainsKey("createdAt"));
    }

    [Fact]
    public void FromPreview_ReturnsEmptyRowsWhenPreviewHasNoDisplayableOutcomes()
    {
        var model = PreviewTableViewModel.FromPreview(new PreviewResult([]), Pipeline());

        Assert.Empty(model.Rows);
        Assert.Equal(["displayName", "total", "createdAt"], model.Columns);
    }

    [Fact]
    public void FromPreview_ReturnsEmptyRowsWhenAllOutcomesAreExcluded()
    {
        var pipeline = Pipeline();
        pipeline.TransformationRules =
        [
            Rule(1, TransformationType.FilterRow, "displayName",
                ("Operator", FilterOperator.Equals.ToString()), ("Value", "Filtered"))
        ];
        var filtered = Processor([new ConditionalFilterTransformationHandler()], [])
            .CreateSession(pipeline)
            .Process(Row(11, ("Name", "Filtered"), ("Amount", 1L), ("When", null)));

        var model = PreviewTableViewModel.FromPreview(new PreviewResult([filtered]), pipeline);

        Assert.Empty(model.Rows);
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
            new SourceFieldDefinition { Name = "Amount" },
            new SourceFieldDefinition { Name = "When" }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Name", TargetField = "displayName" },
            new FieldMapping { SourceField = "Amount", TargetField = "total" },
            new FieldMapping { SourceField = "When", TargetField = "createdAt" }
        ],
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = "displayName"
    };

    private static TransformationRule Rule(
        int order,
        TransformationType type,
        string field,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(), Order = order, Type = type, SourceField = field,
        Configuration = configuration.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
    };

    private static TransformationRule DeduplicateRule(int order, params string[] fields) =>
        Rule(order, TransformationType.Deduplicate, string.Empty, ("Fields", JsonSerializer.Serialize(fields)));

    private static DataRow Row(long number, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = number };
        foreach (var (field, value) in values) row.Values[field] = value;
        return row;
    }
}
