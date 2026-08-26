using System.Runtime.CompilerServices;
using System.Text.Json;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
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
            orchestrator.ExecuteAsync(null!, pipeline, IgnoreBatch, IgnoreProgress, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            orchestrator.ExecuteAsync(source, null!, IgnoreBatch, IgnoreProgress, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            orchestrator.ExecuteAsync(source, pipeline, null!, IgnoreProgress, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            orchestrator.ExecuteAsync(source, pipeline, IgnoreBatch, null!, CancellationToken.None));

        var unreadable = new MemoryStream([1]);
        unreadable.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            orchestrator.ExecuteAsync(unreadable, pipeline, IgnoreBatch, IgnoreProgress, CancellationToken.None));
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
                IgnoreProgress,
                CancellationToken.None));

        Assert.Contains(exception.Problems, problem => problem.Component == "Destination");
        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, extractor.EnumerationCount);
        Assert.Equal(0, callbackInvocations);
    }

    [Fact]
    public async Task ExecuteAsync_ClassifiesInvalidTargetAsReadinessFailureBeforeAccessProbeOrExtraction()
    {
        var extractor = new SequenceExtractor([Row(2, ("Value", "value"))]);
        var resolver = new TrackingResolver(extractor);
        var targetAccess = new RejectedTargetAccessService();
        var orchestrator = Orchestrator(resolver, targetAccessService: targetAccess);
        var callbackInvocations = 0;
        await using var source = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<PipelineNotReadyException>(() =>
            orchestrator.ExecuteAsync(
                source,
                ReadyPipeline(),
                (_, _) =>
                {
                    callbackInvocations++;
                    return Task.CompletedTask;
                },
                IgnoreProgress,
                CancellationToken.None));

        var problem = Assert.Single(
            exception.Problems,
            problem => problem.Component == "Destination");
        Assert.Equal("The configured MongoDB destination name is not valid.", problem.Message);
        Assert.Equal(0, targetAccess.AccessProbeInvocationCount);
        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, extractor.EnumerationCount);
        Assert.Equal(0, callbackInvocations);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesTargetAccessFailureBeforeExtraction()
    {
        var extractor = new SequenceExtractor([Row(2, ("Value", "value"))]);
        var resolver = new TrackingResolver(extractor);
        var targetAccess = new FailingTargetAccessService();
        var orchestrator = Orchestrator(resolver, targetAccessService: targetAccess);
        await using var source = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<MongoTargetAccessException>(() =>
            orchestrator.ExecuteAsync(
                source,
                ReadyPipeline(),
                IgnoreBatch,
                IgnoreProgress,
                CancellationToken.None));

        Assert.Same(targetAccess.Failure, exception);
        Assert.Equal(1, targetAccess.InvocationCount);
        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, extractor.EnumerationCount);
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
        var progress = new List<BatchExecutionProgress>();
        await using var source = new MemoryStream([1]);

        var result = await orchestrator.ExecuteAsync(
            source,
            ReadyPipeline(),
            (batch, _) =>
            {
                emitted.Add(batch);
                return Task.CompletedTask;
            },
            (snapshot, _) =>
            {
                progress.Add(snapshot);
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
        var expectedProgress = rowCount switch
        {
            0 => new[] { (0L, true) },
            2 => new[] { (2L, true) },
            3 => new[] { (3L, false), (3L, true) },
            6 => new[] { (3L, false), (6L, false), (6L, true) },
            7 => new[] { (3L, false), (6L, false), (7L, true) },
            _ => throw new InvalidOperationException("The test case has no expected progress sequence.")
        };
        Assert.Equal(
            expectedProgress,
            progress.Select(snapshot => (snapshot.ProcessedRows, snapshot.IsCompleted)));
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
        var progress = new List<BatchExecutionProgress>();
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
            (snapshot, _) =>
            {
                progress.Add(snapshot);
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
        Assert.All(progress, snapshot => Assert.Equal(
            snapshot.ProcessedRows,
            snapshot.ValidRows + snapshot.InvalidRows + snapshot.FilteredRows + snapshot.DeduplicatedRows));
        Assert.All(
            progress.Zip(progress.Skip(1)),
            pair =>
            {
                Assert.True(pair.First.ProcessedRows <= pair.Second.ProcessedRows);
                Assert.True(pair.First.ValidRows <= pair.Second.ValidRows);
                Assert.True(pair.First.InvalidRows <= pair.Second.InvalidRows);
                Assert.True(pair.First.FilteredRows <= pair.Second.FilteredRows);
                Assert.True(pair.First.DeduplicatedRows <= pair.Second.DeduplicatedRows);
            });
        var completion = Assert.IsType<BatchExecutionProgress>(progress.Last());
        Assert.True(completion.IsCompleted);
        Assert.Equal(result.ProcessedRows, completion.ProcessedRows);
        Assert.Equal(result.ValidRows, completion.ValidRows);
        Assert.Equal(result.InvalidRows, completion.InvalidRows);
        Assert.Equal(result.FilteredRows, completion.FilteredRows);
        Assert.Equal(result.DeduplicatedRows, completion.DeduplicatedRows);
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
            IgnoreProgress,
            CancellationToken.None);

        Assert.Equal([2, 1], batches.Select(batch => batch.Count));
        Assert.Equal(["A", "B", "C"], batches.SelectMany(batch => batch)
            .Select(row => row.Values["id"]));
        Assert.Equal(4, result.ProcessedRows);
        Assert.Equal(3, result.ValidRows);
        Assert.Equal(1, result.DeduplicatedRows);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsFullBatchAndFinalPartialBatchProgress()
    {
        var rows = Enumerable.Range(1, 3)
            .Select(index => Row(index + 1, ("Value", $"value-{index}")))
            .ToArray();
        var progress = new List<BatchExecutionProgress>();
        var callbackOrder = new List<string>();
        await using var source = new MemoryStream([1]);

        await Orchestrator(new SequenceExtractor(rows), batchSize: 2).ExecuteAsync(
            source,
            ReadyPipeline(),
            (_, _) =>
            {
                callbackOrder.Add("batch");
                return Task.CompletedTask;
            },
            (snapshot, _) =>
            {
                callbackOrder.Add("progress");
                progress.Add(snapshot);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Collection(
            progress,
            snapshot =>
            {
                Assert.Equal(2, snapshot.ProcessedRows);
                Assert.Equal(2, snapshot.ValidRows);
                Assert.False(snapshot.IsCompleted);
            },
            snapshot =>
            {
                Assert.Equal(3, snapshot.ProcessedRows);
                Assert.Equal(3, snapshot.ValidRows);
                Assert.True(snapshot.IsCompleted);
            });
        Assert.Equal(["batch", "progress", "batch", "progress"], callbackOrder);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsOneNonFinalSnapshotForAnExactFullBatchEnding()
    {
        var rows = Enumerable.Range(1, 2)
            .Select(index => Row(index + 1, ("Value", $"value-{index}")))
            .ToArray();
        var progress = new List<BatchExecutionProgress>();
        await using var source = new MemoryStream([1]);

        await Orchestrator(new SequenceExtractor(rows), batchSize: 2).ExecuteAsync(
            source,
            ReadyPipeline(),
            IgnoreBatch,
            (snapshot, _) =>
            {
                progress.Add(snapshot);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, progress.Count);
        Assert.All(progress, snapshot => Assert.Equal(2, snapshot.ProcessedRows));
        Assert.False(progress[0].IsCompleted);
        Assert.True(progress[1].IsCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsFinalCompletionForEmptyAndExcludedSources()
    {
        var emptyProgress = new List<BatchExecutionProgress>();
        await using var emptySource = new MemoryStream([1]);

        await Orchestrator(new SequenceExtractor([]), batchSize: 2).ExecuteAsync(
            emptySource,
            ReadyPipeline(),
            IgnoreBatch,
            (snapshot, _) =>
            {
                emptyProgress.Add(snapshot);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var pipeline = ReadyPipeline("Kind", "Value");
        pipeline.FieldMappings = [Mapping("Kind", "kind"), Mapping("Value", "value")];
        pipeline.TransformationRules =
        [
            Rule(
                1,
                TransformationType.FilterRow,
                "kind",
                ("Operator", FilterOperator.Equals.ToString()),
                ("Value", "skip"))
        ];
        pipeline.ValidationRules = [new ValidationRule { Type = ValidationType.Required, Field = "value" }];
        var excludedRows = Enumerable.Range(1, 6)
            .Select(index => Row(
                index + 1,
                ("Kind", index % 2 == 0 ? "skip" : "keep"),
                ("Value", " ")))
            .ToArray();
        var excludedProgress = new List<BatchExecutionProgress>();
        await using var excludedSource = new MemoryStream([1]);

        await Orchestrator(
            new SequenceExtractor(excludedRows),
            batchSize: 3,
            transformations: [new ConditionalFilterTransformationHandler()],
            validations: [new RequiredValidationHandler()]).ExecuteAsync(
                excludedSource,
                pipeline,
                IgnoreBatch,
                (snapshot, _) =>
                {
                    excludedProgress.Add(snapshot);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        var emptyCompletion = Assert.Single(emptyProgress);
        Assert.True(emptyCompletion.IsCompleted);
        Assert.Equal(0, emptyCompletion.ProcessedRows);
        Assert.Equal([3L, 6L, 6L], excludedProgress.Select(snapshot => snapshot.ProcessedRows));
        Assert.Equal([2L, 3L, 3L], excludedProgress.Select(snapshot => snapshot.InvalidRows));
        Assert.Equal([1L, 3L, 3L], excludedProgress.Select(snapshot => snapshot.FilteredRows));
        Assert.All(excludedProgress, snapshot => Assert.Equal(0, snapshot.ValidRows));
        Assert.False(excludedProgress[0].IsCompleted);
        Assert.False(excludedProgress[1].IsCompleted);
        Assert.True(excludedProgress[2].IsCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_AwaitsProgressBeforeContinuingExtractionAndPropagatesFailures()
    {
        var extractor = new SequenceExtractor(
        [
            Row(2, ("Value", "first")),
            Row(3, ("Value", "second"))
        ]);
        var reportStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new MemoryStream([1]);

        var execution = Orchestrator(extractor, batchSize: 1).ExecuteAsync(
            source,
            ReadyPipeline(),
            IgnoreBatch,
            async (_, cancellationToken) =>
            {
                if (reportStarted.TrySetResult(true))
                {
                    await releaseReport.Task.WaitAsync(cancellationToken);
                }
            },
            CancellationToken.None);

        await reportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, extractor.YieldedRows);
        Assert.False(execution.IsCompleted);

        releaseReport.SetResult(true);
        await execution;

        var failingExtractor = new SequenceExtractor(
        [
            Row(2, ("Value", "first")),
            Row(3, ("Value", "second"))
        ]);
        var progressFailure = new ApplicationException("Progress update failed.");
        await using var failingSource = new MemoryStream([1]);

        var actualFailure = await Assert.ThrowsAsync<ApplicationException>(() =>
            Orchestrator(failingExtractor, batchSize: 1).ExecuteAsync(
                failingSource,
                ReadyPipeline(),
                IgnoreBatch,
                (_, _) => Task.FromException(progressFailure),
                CancellationToken.None));

        Assert.Same(progressFailure, actualFailure);
        Assert.Equal(1, failingExtractor.YieldedRows);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellationDuringProgressWithoutCompletion()
    {
        var reportStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<BatchExecutionProgress>();
        using var cancellation = new CancellationTokenSource();
        var observedToken = CancellationToken.None;
        await using var source = new MemoryStream([1]);

        var execution = Orchestrator(
            new SequenceExtractor([Row(2, ("Value", "value"))]),
            batchSize: 1).ExecuteAsync(
                source,
                ReadyPipeline(),
                IgnoreBatch,
                async (snapshot, cancellationToken) =>
                {
                    progress.Add(snapshot);
                    observedToken = cancellationToken;
                    reportStarted.SetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                cancellation.Token);

        await reportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(cancellation.Token, observedToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.All(progress, snapshot => Assert.False(snapshot.IsCompleted));
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
            IgnoreProgress,
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
                IgnoreProgress,
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
        var progressCallbackInvocations = 0;
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
                (_, _) =>
                {
                    progressCallbackInvocations++;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Same(callbackFailure, actualCallbackFailure);
        Assert.Equal(2, callbackExtractor.YieldedRows);
        Assert.Equal(1, callbackInvocations);
        Assert.Equal(0, progressCallbackInvocations);
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
                IgnoreProgress,
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
            IgnoreProgress,
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
                IgnoreProgress,
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
                IgnoreProgress,
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
                    IgnoreProgress,
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

    private static Task IgnoreProgress(
        BatchExecutionProgress progress,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private static BatchOrchestrator Orchestrator(
        IFileExtractor extractor,
        int batchSize = 1000,
        IEnumerable<ITransformationHandler>? transformations = null,
        IEnumerable<IValidationHandler>? validations = null,
        IMongoTargetAccessService? targetAccessService = null) =>
        Orchestrator(
            new TrackingResolver(extractor),
            batchSize,
            transformations,
            validations,
            targetAccessService);

    private static BatchOrchestrator Orchestrator(
        IFileExtractorResolver resolver,
        int batchSize = 1000,
        IEnumerable<ITransformationHandler>? transformations = null,
        IEnumerable<IValidationHandler>? validations = null,
        IMongoTargetAccessService? targetAccessService = null)
    {
        var mappingService = new FieldMappingService();
        var resolvedTargetAccessService =
            targetAccessService ?? AllowedTargetAccessService.Instance;
        return new BatchOrchestrator(
            resolver,
            new PipelineReadinessService(
                new NullRepository(),
                mappingService,
                resolvedTargetAccessService),
            new PipelineRowProcessor(
                mappingService,
                new TransformationEngine(new TransformationHandlerRegistry(transformations ?? [])),
                new ValidationEngine(new ValidationHandlerRegistry(validations ?? []))),
            resolvedTargetAccessService,
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

    private sealed class AllowedTargetAccessService : IMongoTargetAccessService
    {
        public static AllowedTargetAccessService Instance { get; } = new();

        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FailingTargetAccessService : IMongoTargetAccessService
    {
        public MongoTargetAccessException Failure { get; } =
            new();

        public int InvocationCount { get; private set; }

        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromException(Failure);
        }
    }

    private sealed class RejectedTargetAccessService : IMongoTargetAccessService
    {
        public int AccessProbeInvocationCount { get; private set; }

        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Rejected(
                "The configured MongoDB destination name is not valid.");

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken)
        {
            AccessProbeInvocationCount++;
            throw new InvalidOperationException("The access probe must not run for an invalid target.");
        }
    }
}
