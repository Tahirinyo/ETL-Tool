using System.Runtime.CompilerServices;
using System.Text.Json;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Preview;

public sealed class PreviewServiceTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task PreviewAsync_ProcessesAtMostFirstOneHundredSourceRows(
        int availableRows,
        int expectedRows)
    {
        var extractor = new GuardedExtractor(availableRows, throwIfRow101IsRequested: true);
        var service = Service(extractor, Processor([], []));
        await using var source = new MemoryStream([1]);

        var result = await service.PreviewAsync(source, ReadyPipeline(), CancellationToken.None);

        Assert.Equal(expectedRows, result.Rows.Count);
        Assert.Equal(expectedRows, extractor.YieldedRows);
        Assert.All(result.Rows, row => Assert.Equal(RowProcessingStatus.Valid, row.Status));
        Assert.Equal(expectedRows, result.ValidRowCount);
        Assert.Equal(0, result.InvalidRowCount);
        Assert.Equal(0, result.FilteredRowCount);
        Assert.Equal(expectedRows, extractor.YieldedRows);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task PreviewAsync_ReportsAllInvalidAndAllFilteredCounters()
    {
        var invalidExtractor = new GuardedExtractor(3, sourceValue: " ");
        var invalidPipeline = ReadyPipeline();
        invalidPipeline.ValidationRules =
        [
            new ValidationRule { Type = ValidationType.Required, Field = "value" }
        ];
        await using var invalidSource = new MemoryStream([1]);

        var invalid = await Service(
                invalidExtractor,
                Processor([], [new RequiredValidationHandler()]))
            .PreviewAsync(invalidSource, invalidPipeline, CancellationToken.None);

        Assert.Equal(0, invalid.ValidRowCount);
        Assert.Equal(3, invalid.InvalidRowCount);
        Assert.Equal(0, invalid.FilteredRowCount);

        var filteredPipeline = ReadyPipeline();
        filteredPipeline.TransformationRules =
        [
            Rule(
                1,
                TransformationType.FilterRow,
                "value",
                ("Operator", FilterOperator.Equals.ToString()),
                ("Value", "filtered"))
        ];
        var filteredExtractor = new GuardedExtractor(3, sourceValue: "filtered");
        await using var filteredSource = new MemoryStream([1]);

        var filtered = await Service(
                filteredExtractor,
                Processor([new ConditionalFilterTransformationHandler()], []))
            .PreviewAsync(filteredSource, filteredPipeline, CancellationToken.None);

        Assert.Equal(0, filtered.ValidRowCount);
        Assert.Equal(0, filtered.InvalidRowCount);
        Assert.Equal(3, filtered.FilteredRowCount);
    }

    [Fact]
    public async Task PreviewAsync_RejectsUnreadyPipelineBeforeResolvingOrReadingSource()
    {
        var extractor = new GuardedExtractor(1);
        var resolver = new TrackingResolver(extractor);
        var service = Service(resolver, Processor([], []));
        var pipeline = ReadyPipeline();
        pipeline.DestinationDatabase = " ";
        await using var source = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<PipelineNotReadyException>(() =>
            service.PreviewAsync(source, pipeline, CancellationToken.None));

        Assert.Equal("The pipeline is not ready for preview.", exception.Message);
        Assert.Contains(exception.Problems, problem => problem.Component == "Destination");
        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, extractor.EnumerationCount);
    }

    [Fact]
    public async Task PreviewAsync_PropagatesExtractionAndMappingFailures()
    {
        var extractionFailure = new InvalidDataException("Malformed source.");
        var failingExtractor = new GuardedExtractor(1, extractionFailure: extractionFailure);
        await using var firstSource = new MemoryStream([1]);

        var actual = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Service(failingExtractor, Processor([], [])).PreviewAsync(
                firstSource,
                ReadyPipeline(),
                CancellationToken.None));

        Assert.Same(extractionFailure, actual);

        var wrongSchemaExtractor = new GuardedExtractor(1, sourceField: "Different");
        await using var secondSource = new MemoryStream([1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(wrongSchemaExtractor, Processor([], [])).PreviewAsync(
                secondSource,
                ReadyPipeline(),
                CancellationToken.None));
    }

    [Fact]
    public async Task PreviewAsync_PropagatesCancellationBeforeAndDuringExtraction()
    {
        var before = new CancellationToken(canceled: true);
        var untouchedExtractor = new GuardedExtractor(1);
        await using var firstSource = new MemoryStream([1]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(untouchedExtractor, Processor([], [])).PreviewAsync(
                firstSource,
                ReadyPipeline(),
                before));
        Assert.Equal(0, untouchedExtractor.EnumerationCount);

        using var cancellation = new CancellationTokenSource();
        var cancellingExtractor = new GuardedExtractor(
            3,
            beforeYield: rowNumber =>
            {
                if (rowNumber == 2)
                {
                    cancellation.Cancel();
                }
            });
        await using var secondSource = new MemoryStream([1]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(cancellingExtractor, Processor([], [])).PreviewAsync(
                secondSource,
                ReadyPipeline(),
                cancellation.Token));
        Assert.Equal(1, cancellingExtractor.YieldedRows);
    }

    [Fact]
    public async Task PreviewAsync_UsesSameSharedRowProcessingSession()
    {
        var pipeline = ReadyPipeline();
        pipeline.TransformationRules =
        [
            new TransformationRule
            {
                Id = Guid.NewGuid(),
                Type = TransformationType.Trim,
                Order = 1,
                SourceField = "value"
            }
        ];
        var processor = Processor([new TrimTransformationHandler()], []);
        var direct = processor.CreateSession(pipeline).Process(Row(2, ("Value", " preview ")));
        var extractor = new GuardedExtractor(1, sourceValue: " preview ");
        await using var source = new MemoryStream([1]);

        var preview = await Service(extractor, processor).PreviewAsync(
            source,
            pipeline,
            CancellationToken.None);

        var previewRow = Assert.Single(preview.Rows);
        Assert.Equal(direct.Status, previewRow.Status);
        Assert.Equal(direct.Row.Values, previewRow.Row.Values);
        Assert.Equal(direct.Errors, previewRow.Errors);
    }

    [Fact]
    public async Task PreviewAsync_KeepsFirstValidationValidRowForConfiguredDeduplication()
    {
        var pipeline = ReadyPipeline();
        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id" },
            new SourceFieldDefinition { Name = "Value" }
        ];
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
            new FieldMapping { SourceField = "Value", TargetField = "value", IsIncluded = true }
        ];
        pipeline.UpsertKeyField = "id";
        pipeline.TransformationRules = [DeduplicateRule(1, "id")];
        pipeline.ValidationRules =
        [
            new ValidationRule { Type = ValidationType.Required, Field = "value" }
        ];
        var extractor = new SequenceExtractor(
            Row(2, ("Id", "A"), ("Value", null)),
            Row(3, ("Id", "A"), ("Value", "first valid")),
            Row(4, ("Id", "A"), ("Value", "later valid")));
        await using var source = new MemoryStream([1]);

        var preview = await Service(
                extractor,
                Processor(
                    [new DeduplicateTransformationHandler()],
                    [new RequiredValidationHandler()]))
            .PreviewAsync(source, pipeline, CancellationToken.None);

        Assert.Equal(
            [RowProcessingStatus.Invalid, RowProcessingStatus.Valid, RowProcessingStatus.Duplicate],
            preview.Rows.Select(row => row.Status));
        Assert.Equal([2L, 3L, 4L], preview.Rows.Select(row => row.Row.SourceRowNumber));
        Assert.Equal(1, preview.InvalidRowCount);
        Assert.Equal(1, preview.ValidRowCount);
        Assert.Equal(0, preview.FilteredRowCount);
    }

    [Fact]
    public async Task PreviewAsync_StopsAfterOneHundredSourceRowsWhenOutcomesAreNotAllValid()
    {
        var pipeline = ReadyPipeline();
        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Kind" },
            new SourceFieldDefinition { Name = "Id" },
            new SourceFieldDefinition { Name = "Value" }
        ];
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Kind", TargetField = "kind", IsIncluded = true },
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
            new FieldMapping { SourceField = "Value", TargetField = "value", IsIncluded = true }
        ];
        pipeline.UpsertKeyField = "id";
        pipeline.TransformationRules =
        [
            Rule(
                1,
                TransformationType.FilterRow,
                "kind",
                ("Operator", FilterOperator.Equals.ToString()),
                ("Value", "filtered")),
            DeduplicateRule(2, "id"),
            Rule(3, TransformationType.ConvertToInteger, "value")
        ];
        pipeline.ValidationRules = [new ValidationRule { Type = ValidationType.Required, Field = "value" }];
        var validation = new TrackingRequiredHandler();
        var extractor = new OutcomeExtractor();
        await using var source = new MemoryStream([1]);

        var result = await Service(
                extractor,
                Processor(
                    [
                        new ConditionalFilterTransformationHandler(),
                        new DeduplicateTransformationHandler(),
                        new ConvertToIntegerTransformationHandler()
                    ],
                    [validation]))
            .PreviewAsync(source, pipeline, CancellationToken.None);

        Assert.Equal(100, result.Rows.Count);
        Assert.Equal(100, extractor.YieldedRows);
        Assert.Equal(20, result.Rows.Count(row => row.Status == RowProcessingStatus.Valid));
        Assert.Equal(20, result.Rows.Count(row => row.Status == RowProcessingStatus.Filtered));
        Assert.Equal(20, result.Rows.Count(row => row.Status == RowProcessingStatus.Duplicate));
        Assert.Equal(40, result.Rows.Count(row => row.Status == RowProcessingStatus.Invalid));
        Assert.Equal(20, result.ValidRowCount);
        Assert.Equal(40, result.InvalidRowCount);
        Assert.Equal(20, result.FilteredRowCount);
        Assert.Equal(20, result.ValidRowCount);
        Assert.Equal(40, result.InvalidRowCount);
        Assert.Equal(20, result.FilteredRowCount);
        Assert.Equal(100, extractor.YieldedRows);
        Assert.Equal(20, result.Rows.Count(row =>
            row.Errors.SingleOrDefault()?.Stage == RowProcessingErrorStage.Transformation));
        Assert.Equal(20, result.Rows.Count(row =>
            row.Errors.SingleOrDefault()?.Stage == RowProcessingErrorStage.Validation));
        Assert.Equal(40, validation.InvocationCount);
        Assert.Equal(
            Enumerable.Range(2, 100).Select(number => (long)number).ToArray(),
            result.Rows.Select(row => row.Row.SourceRowNumber).ToArray());
    }

    private static PreviewService Service(
        IFileExtractor extractor,
        PipelineRowProcessor processor) =>
        Service(new TrackingResolver(extractor), processor);

    private static PreviewService Service(
        IFileExtractorResolver resolver,
        PipelineRowProcessor processor) => new(
            resolver,
            new PipelineReadinessService(new NullRepository(), new FieldMappingService()),
            processor);

    private static PipelineRowProcessor Processor(
        IEnumerable<ITransformationHandler> transformations,
        IEnumerable<IValidationHandler> validations) => new(
            new FieldMappingService(),
            new TransformationEngine(new TransformationHandlerRegistry(transformations)),
            new ValidationEngine(new ValidationHandlerRegistry(validations)));

    private static PipelineDefinition ReadyPipeline() => new()
    {
        Id = Guid.NewGuid(),
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema = [new SourceFieldDefinition { Name = "Value" }],
        FieldMappings =
        [
            new FieldMapping
            {
                SourceField = "Value",
                TargetField = "value",
                IsIncluded = true
            }
        ],
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = "value"
    };

    private static DataRow Row(
        long rowNumber,
        params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = rowNumber };
        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }

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

    private sealed class TrackingResolver(IFileExtractor extractor) : IFileExtractorResolver
    {
        public int InvocationCount { get; private set; }

        public IFileExtractor Resolve(SourceType sourceType)
        {
            InvocationCount++;
            Assert.Equal(extractor.SourceType, sourceType);
            return extractor;
        }
    }

    private sealed class GuardedExtractor(
        int availableRows,
        bool throwIfRow101IsRequested = false,
        string sourceField = "Value",
        string sourceValue = "value",
        Exception? extractionFailure = null,
        Action<int>? beforeYield = null) : IFileExtractor
    {
        public SourceType SourceType => SourceType.Csv;

        public int EnumerationCount { get; private set; }

        public int YieldedRows { get; private set; }

        public Task<IReadOnlyList<string>> ReadHeadersAsync(
            Stream stream,
            SourceOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([sourceField]);

        public async IAsyncEnumerable<DataRow> ReadAsync(
            Stream stream,
            SourceOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            EnumerationCount++;
            await Task.CompletedTask;

            if (extractionFailure is not null)
            {
                throw extractionFailure;
            }

            for (var index = 1; index <= availableRows; index++)
            {
                if (throwIfRow101IsRequested && index > 100)
                {
                    throw new InvalidOperationException("Preview requested source row 101.");
                }

                beforeYield?.Invoke(index);
                cancellationToken.ThrowIfCancellationRequested();
                YieldedRows++;
                yield return Row(index + 1, (sourceField, sourceValue));
            }
        }
    }

    private sealed class OutcomeExtractor : IFileExtractor
    {
        public SourceType SourceType => SourceType.Csv;

        public int YieldedRows { get; private set; }

        public Task<IReadOnlyList<string>> ReadHeadersAsync(
            Stream stream,
            SourceOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["Kind", "Id", "Value"]);

        public async IAsyncEnumerable<DataRow> ReadAsync(
            Stream stream,
            SourceOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            for (var sourceIndex = 1; ; sourceIndex++)
            {
                if (sourceIndex == 101)
                {
                    throw new InvalidOperationException("Preview requested source row 101.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                YieldedRows++;
                var cycle = (sourceIndex - 1) / 5;
                var outcomeIndex = (sourceIndex - 1) % 5;
                yield return outcomeIndex switch
                {
                    0 => Row(sourceIndex + 1, ("Kind", "filtered"), ("Id", $"F{cycle}"), ("Value", "1")),
                    1 => Row(sourceIndex + 1, ("Kind", "keep"), ("Id", $"V{cycle}"), ("Value", "1")),
                    2 => Row(sourceIndex + 1, ("Kind", "keep"), ("Id", $"I{cycle}"), ("Value", null)),
                    3 => Row(sourceIndex + 1, ("Kind", "keep"), ("Id", $"V{cycle}"), ("Value", "2")),
                    _ => Row(sourceIndex + 1, ("Kind", "keep"), ("Id", $"T{cycle}"), ("Value", "not-a-number"))
                };
            }
        }
    }

    private sealed class SequenceExtractor(params DataRow[] rows) : IFileExtractor
    {
        public SourceType SourceType => SourceType.Csv;

        public Task<IReadOnlyList<string>> ReadHeadersAsync(
            Stream stream,
            SourceOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public async IAsyncEnumerable<DataRow> ReadAsync(
            Stream stream,
            SourceOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }
    }

    private sealed class TrackingRequiredHandler : IValidationHandler
    {
        public ValidationType Type => ValidationType.Required;

        public int InvocationCount { get; private set; }

        public ValidationResult Validate(DataRow row, ValidationRule rule)
        {
            InvocationCount++;
            return row.Values[rule.Field] is null
                ? ValidationResult.Invalid(row, new ValidationError(rule.Field, "Value is required."))
                : ValidationResult.Valid(row);
        }
    }

    private sealed class NullRepository : IPipelineDefinitionRepository
    {
        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
