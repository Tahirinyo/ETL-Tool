using System.Runtime.CompilerServices;
using System.Text.Json;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Execution;

public sealed class BatchOrchestratorTests
{
    [Fact]
    public void Options_DefaultBatchSizeIsOneThousand()
    {
        Assert.Equal(1000, new BatchExecutionOptions().BatchSize);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsInvalidArgumentsAndUnreadableSource()
    {
        var orchestrator = Orchestrator(new SequenceExtractor([]));
        var pipeline = ReadyPipeline();
        await using var source = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            orchestrator.ExecuteAsync(null!, pipeline, IgnoreBatch, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            orchestrator.ExecuteAsync(source, null!, IgnoreBatch, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            orchestrator.ExecuteAsync(source, pipeline, null!, CancellationToken.None));

        var unreadable = new MemoryStream([1]);
        unreadable.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            orchestrator.ExecuteAsync(unreadable, pipeline, IgnoreBatch, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonpositiveBatchSize(int batchSize)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Orchestrator(new SequenceExtractor([]), batchSize));

        Assert.Contains("EtlExecution:BatchSize", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnreadyPipelineBeforeExtractionOrCallback()
    {
        var extractor = new SequenceExtractor([Row(2, ("Value", "value"))]);
        var resolver = new TrackingResolver(extractor);
        var orchestrator = Orchestrator(resolver);
        var pipeline = ReadyPipeline();
        pipeline.DestinationDatabase = " ";
        var callbackInvocations = 0;
        await using var source = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<PipelineNotReadyException>(() =>
            orchestrator.ExecuteAsync(
                source,
                pipeline,
                (_, _) =>
                {
                    callbackInvocations++;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Contains(exception.Problems, problem => problem.Component == "Destination");
        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, extractor.EnumerationCount);
        Assert.Equal(0, callbackInvocations);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(2, "2")]
    [InlineData(3, "3")]
    [InlineData(6, "3,3")]
    [InlineData(7, "3,3,1")]
    public async Task ExecuteAsync_EmitsExpectedFullAndPartialBatches(
        int rowCount,
        string expectedBatchSizes)
    {
        var rows = Enumerable.Range(1, rowCount)
            .Select(index => Row(index + 1, ("Value", $"value-{index}")))
            .ToArray();
        var orchestrator = Orchestrator(new SequenceExtractor(rows), batchSize: 3);
        var emitted = new List<IReadOnlyList<DataRow>>();
        await using var source = new MemoryStream([1]);

        var result = await orchestrator.ExecuteAsync(
            source,
            ReadyPipeline(),
            (batch, _) =>
            {
                emitted.Add(batch);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var expected = string.IsNullOrEmpty(expectedBatchSizes)
            ? []
            : expectedBatchSizes.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(expected, emitted.Select(batch => batch.Count));
        Assert.Equal(Enumerable.Range(1, rowCount), emitted.SelectMany(batch => batch)
            .Select(row => int.Parse(((string)row.Values["value"]!).AsSpan("value-".Length))));
        Assert.Equal(rowCount, result.ProcessedRows);
        Assert.Equal(rowCount, result.ValidRows);
        Assert.Equal(0, result.InvalidRows);
        Assert.Equal(0, result.FilteredRows);
        Assert.Equal(0, result.DeduplicatedRows);
        Assert.Equal(rowCount == 0 ? 0 : (rowCount + 2) / 3, emitted.Count);
    }

    [Fact]
    public async Task ExecuteAsync_UsesSharedProcessingAndClassifiesEveryOutcome()
    {
        var pipeline = ReadyPipeline("Kind", "Id", "Value");
        pipeline.SourceOptions.CultureName = "tr-TR";
        pipeline.FieldMappings =
        [
            Mapping("Kind", "kind"),
            Mapping("Id", "id"),
            Mapping("Value", "value")
        ];
        pipeline.UpsertKeyField = "id";
        pipeline.TransformationRules =
        [
            Rule(
                1,
                TransformationType.FilterRow,
                "kind",
                ("Operator", FilterOperator.Equals.ToString()),
                ("Value", "skip")),
            DeduplicateRule(2, "id"),
            Rule(3, TransformationType.Trim, "value"),
            Rule(4, TransformationType.ConvertToInteger, "value")
        ];
        pipeline.ValidationRules =
        [
            new ValidationRule { Type = ValidationType.Required, Field = "value" }
        ];
        var rows = new[]
        {
            Row(2, ("Kind", "keep"), ("Id", "A"), ("Value", " 1.234 ")),
            Row(3, ("Kind", "keep"), ("Id", "B"), ("Value", null)),
            Row(4, ("Kind", "skip"), ("Id", "C"), ("Value", "ignored")),
            Row(5, ("Kind", "keep"), ("Id", "A"), ("Value", "9")),
            Row(6, ("Kind", "keep"), ("Id", "B"), ("Value", " 2.345 ")),
            Row(7, ("Kind", "keep"), ("Id", "B"), ("Value", "8")),
            Row(8, ("Kind", "keep"), ("Id", "D"), ("Value", "not-a-number"))
        };
        var batches = new List<IReadOnlyList<DataRow>>();
        var orchestrator = Orchestrator(
            new SequenceExtractor(rows),
            batchSize: 1,
            transformations:
            [
                new ConditionalFilterTransformationHandler(),
                new DeduplicateTransformationHandler(),
                new TrimTransformationHandler(),
                new ConvertToIntegerTransformationHandler()
            ],
            validations: [new RequiredValidationHandler()]);
        await using var source = new MemoryStream([1]);

        var result = await orchestrator.ExecuteAsync(
            source,
            pipeline,
            (batch, _) =>
            {
                batches.Add(batch);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal([2L, 6L], batches.SelectMany(batch => batch).Select(row => row.SourceRowNumber));
        Assert.Equal([1234L, 2345L], batches.SelectMany(batch => batch)
            .Select(row => row.Values["value"]));
        Assert.Equal(7, result.ProcessedRows);
        Assert.Equal(2, result.ValidRows);
        Assert.Equal(2, result.InvalidRows);
        Assert.Equal(1, result.FilteredRows);
        Assert.Equal(2, result.DeduplicatedRows);
        Assert.Equal(
            result.ProcessedRows,
            result.ValidRows + result.InvalidRows + result.FilteredRows + result.DeduplicatedRows);
    }

    [Fact]
    public async Task ExecuteAsync_KeepsConfiguredDeduplicationAcrossBatchBoundaries()
    {
        var pipeline = ReadyPipeline("Id");
        pipeline.FieldMappings = [Mapping("Id", "id")];
        pipeline.UpsertKeyField = "id";
        pipeline.TransformationRules = [DeduplicateRule(1, "id")];
        var rows = new[]
        {
            Row(2, ("Id", "A")),
            Row(3, ("Id", "B")),
            Row(4, ("Id", "A")),
            Row(5, ("Id", "C"))
        };
        var batches = new List<IReadOnlyList<DataRow>>();
        var orchestrator = Orchestrator(
            new SequenceExtractor(rows),
            batchSize: 2,
            transformations: [new DeduplicateTransformationHandler()]);
        await using var source = new MemoryStream([1]);

        var result = await orchestrator.ExecuteAsync(
            source,
            pipeline,
            (batch, _) =>
            {
                batches.Add(batch);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal([2, 1], batches.Select(batch => batch.Count));
        Assert.Equal(["A", "B", "C"], batches.SelectMany(batch => batch)
            .Select(row => row.Values["id"]));
        Assert.Equal(4, result.ProcessedRows);
        Assert.Equal(3, result.ValidRows);
        Assert.Equal(1, result.DeduplicatedRows);
    }

    [Fact]
    public async Task ExecuteAsync_AwaitsCallbackAndDoesNotReuseEmittedBatch()
    {
        var extractor = new SequenceExtractor(
            Enumerable.Range(1, 3)
                .Select(index => Row(index + 1, ("Value", $"value-{index}")))
                .ToArray());
        var orchestrator = Orchestrator(extractor, batchSize: 2);
        var callbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<IReadOnlyList<DataRow>>();
        await using var source = new MemoryStream([1]);

        var execution = orchestrator.ExecuteAsync(
            source,
            ReadyPipeline(),
            async (batch, cancellationToken) =>
            {
                batches.Add(batch);
                if (batches.Count == 1)
                {
                    callbackStarted.SetResult(true);
                    await releaseCallback.Task.WaitAsync(cancellationToken);
                }
            },
            CancellationToken.None);

        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, extractor.YieldedRows);
        Assert.False(execution.IsCompleted);

        releaseCallback.SetResult(true);
        await execution;

        Assert.Equal(3, extractor.YieldedRows);
        Assert.Equal([2, 1], batches.Select(batch => batch.Count));
        Assert.NotSame(batches[0], batches[1]);
        Assert.Equal([2L, 3L], batches[0].Select(row => row.SourceRowNumber));
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesExtractionAndCallbackFailuresImmediately()
    {
        var extractionFailure = new InvalidDataException("Malformed source.");
        var failingExtractor = new SequenceExtractor(
            [Row(2, ("Value", "first"))],
            terminalFailure: extractionFailure);
        var extractionCallbackInvocations = 0;
        await using var firstSource = new MemoryStream([1]);

        var actualExtractionFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Orchestrator(failingExtractor, batchSize: 2).ExecuteAsync(
                firstSource,
                ReadyPipeline(),
                (_, _) =>
                {
                    extractionCallbackInvocations++;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Same(extractionFailure, actualExtractionFailure);
        Assert.Equal(1, failingExtractor.YieldedRows);
        Assert.Equal(0, extractionCallbackInvocations);

        var callbackFailure = new ApplicationException("Batch failed.");
        var callbackExtractor = new SequenceExtractor(
            Enumerable.Range(1, 4)
                .Select(index => Row(index + 1, ("Value", $"value-{index}")))
                .ToArray());
        var callbackInvocations = 0;
        await using var secondSource = new MemoryStream([1]);

        var actualCallbackFailure = await Assert.ThrowsAsync<ApplicationException>(() =>
            Orchestrator(callbackExtractor, batchSize: 2).ExecuteAsync(
                secondSource,
                ReadyPipeline(),
                (_, _) =>
                {
                    callbackInvocations++;
                    return Task.FromException(callbackFailure);
                },
                CancellationToken.None));

        Assert.Same(callbackFailure, actualCallbackFailure);
        Assert.Equal(2, callbackExtractor.YieldedRows);
        Assert.Equal(1, callbackInvocations);
        Assert.True(secondSource.CanRead);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesUnexpectedSharedProcessingFailure()
    {
        var extractor = new SequenceExtractor(
        [
            Row(2, ("Unexpected", "first")),
            Row(3, ("Value", "later"))
        ]);
        var callbackInvocations = 0;
        await using var source = new MemoryStream([1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Orchestrator(extractor).ExecuteAsync(
                source,
                ReadyPipeline(),
                (_, _) =>
                {
                    callbackInvocations++;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal(0, callbackInvocations);
        Assert.Equal(1, extractor.YieldedRows);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellationTokenAndLeavesSourceOpen()
    {
        var extractor = new SequenceExtractor([Row(2, ("Value", "value"))]);
        var orchestrator = Orchestrator(extractor, batchSize: 1);
        using var cancellation = new CancellationTokenSource();
        var callbackToken = CancellationToken.None;
        await using var source = new MemoryStream([1]);

        await orchestrator.ExecuteAsync(
            source,
            ReadyPipeline(),
            (_, token) =>
            {
                callbackToken = token;
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(cancellation.Token, extractor.ObservedToken);
        Assert.Equal(cancellation.Token, callbackToken);
        Assert.True(source.CanRead);

        cancellation.Cancel();
        var untouchedExtractor = new SequenceExtractor([Row(2, ("Value", "value"))]);
        await using var cancelledSource = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Orchestrator(untouchedExtractor).ExecuteAsync(
                cancelledSource,
                ReadyPipeline(),
                IgnoreBatch,
                cancellation.Token));
        Assert.Equal(0, untouchedExtractor.EnumerationCount);
    }

    [Fact]
    public async Task ExecuteAsync_ObservesRegistryCancellationBeforeExecutionStarts()
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();
        var extractor = new SequenceExtractor([Row(2, ("Value", "value"))]);
        using var registration = registry.Register(runId);
        await using var source = new MemoryStream([1]);

        Assert.True(registry.TryRequestCancellation(runId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Orchestrator(extractor).ExecuteAsync(
                source,
                ReadyPipeline(),
                IgnoreBatch,
                registration.Token));

        Assert.Equal(0, extractor.EnumerationCount);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesRegistryCancellationDuringTokenAwareBatchCallback()
    {
        var registry = new ExecutionCancellationRegistry();
        var runId = Guid.NewGuid();
        var registration = registry.Register(runId);
        var callbackStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new MemoryStream([1]);

        try
        {
            var execution = Orchestrator(
                new SequenceExtractor([Row(2, ("Value", "value"))]),
                batchSize: 1).ExecuteAsync(
                    source,
                    ReadyPipeline(),
                    async (_, cancellationToken) =>
                    {
                        callbackStarted.SetResult(true);
                        await releaseCallback.Task.WaitAsync(cancellationToken);
                    },
                    registration.Token);

            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(registry.TryRequestCancellation(runId));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.True(registry.TryRequestCancellation(runId));
        }
        finally
        {
            registration.Dispose();
        }

        Assert.False(registry.TryRequestCancellation(runId));
    }

    private static Task IgnoreBatch(
        IReadOnlyList<DataRow> batch,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private static BatchOrchestrator Orchestrator(
        IFileExtractor extractor,
        int batchSize = 1000,
        IEnumerable<ITransformationHandler>? transformations = null,
        IEnumerable<IValidationHandler>? validations = null) =>
        Orchestrator(
            new TrackingResolver(extractor),
            batchSize,
            transformations,
            validations);

    private static BatchOrchestrator Orchestrator(
        IFileExtractorResolver resolver,
        int batchSize = 1000,
        IEnumerable<ITransformationHandler>? transformations = null,
        IEnumerable<IValidationHandler>? validations = null)
    {
        var mappingService = new FieldMappingService();
        return new BatchOrchestrator(
            resolver,
            new PipelineReadinessService(new NullRepository(), mappingService),
            new PipelineRowProcessor(
                mappingService,
                new TransformationEngine(new TransformationHandlerRegistry(transformations ?? [])),
                new ValidationEngine(new ValidationHandlerRegistry(validations ?? []))),
            new BatchExecutionOptions { BatchSize = batchSize });
    }

    private static PipelineDefinition ReadyPipeline(params string[] sourceFields) => new()
    {
        Id = Guid.NewGuid(),
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema = (sourceFields.Length == 0 ? ["Value"] : sourceFields)
            .Select(field => new SourceFieldDefinition { Name = field })
            .ToList(),
        FieldMappings = [Mapping("Value", "value")],
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = "value"
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

    private sealed class SequenceExtractor(
        IReadOnlyList<DataRow> rows,
        Exception? terminalFailure = null) : IFileExtractor
    {
        public SourceType SourceType => SourceType.Csv;

        public int EnumerationCount { get; private set; }

        public int YieldedRows { get; private set; }

        public CancellationToken ObservedToken { get; private set; }

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
            EnumerationCount++;
            ObservedToken = cancellationToken;
            await Task.Yield();

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                YieldedRows++;
                yield return row;
            }

            if (terminalFailure is not null)
            {
                throw terminalFailure;
            }
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
