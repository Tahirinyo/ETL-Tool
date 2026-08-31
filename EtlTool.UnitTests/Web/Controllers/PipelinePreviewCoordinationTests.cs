using System.Runtime.CompilerServices;
using System.Text;
using EtlTool.Application.Extraction;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinePreviewCoordinationTests
{
    [Fact]
    public async Task Preview_WaitsForPersistedReplacementToActivateBeforeCapturingSnapshot()
    {
        var pipelineId = Guid.NewGuid();
        var initial = Pipeline(pipelineId, "S0");
        var replacement = Pipeline(pipelineId, "S1");
        var pipelineService = new MutablePipelineService(initial);
        var store = new CoordinatedSourceStore();
        store.SeedActive(pipelineId, "same.csv", initial.SourceOptions, "F0");
        var replacementReferenceId = store.Stage(
            pipelineId,
            "same.csv",
            replacement.SourceOptions,
            "F1");
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var persistenceEntered = CompletionSource();
        var releasePersistence = CompletionSource();
        string? capturedPipeline = null;
        string? capturedSource = null;
        var previewService = new RecordingPreviewService(async (source, pipeline, cancellationToken) =>
        {
            capturedPipeline = pipeline.Name;
            await foreach (var row in source.ReadAsync(cancellationToken))
            {
                capturedSource = Assert.IsType<string>(row.Values["Content"]);
            }
            return new PreviewResult([]);
        });
        var controller = Controller(pipelineService, store, coordinator, previewService);

        var commit = coordinator.CommitAsync(
            pipelineId,
            replacementReferenceId,
            async cancellationToken =>
            {
                pipelineService.Current = replacement;
                persistenceEntered.TrySetResult();
                await releasePersistence.Task.WaitAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);
        await persistenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var preview = controller.Preview(pipelineId, CancellationToken.None);
        Assert.False(preview.IsCompleted);
        Assert.Equal(0, store.AcquireCallCount);

        releasePersistence.TrySetResult();
        Assert.Equal(PipelineSourceCommitStatus.Succeeded, await commit);
        Assert.IsType<ViewResult>(await preview);

        Assert.Equal("S1", capturedPipeline);
        Assert.Equal("F1", capturedSource);
        Assert.Equal(0, store.ActiveLeaseCount);
    }

    [Fact]
    public async Task Preview_ReleasesGateAfterCapturingOldPipelineAndSourceLease()
    {
        var pipelineId = Guid.NewGuid();
        var initial = Pipeline(pipelineId, "S0");
        var replacement = Pipeline(pipelineId, "S1");
        var pipelineService = new MutablePipelineService(initial);
        var store = new CoordinatedSourceStore();
        store.SeedActive(pipelineId, "same.csv", initial.SourceOptions, "F0");
        var replacementReferenceId = store.Stage(
            pipelineId,
            "same.csv",
            replacement.SourceOptions,
            "F1");
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var previewProcessingEntered = CompletionSource();
        var releasePreviewProcessing = CompletionSource();
        string? capturedPipeline = null;
        string? capturedSource = null;
        var previewService = new RecordingPreviewService(async (source, pipeline, cancellationToken) =>
        {
            capturedPipeline = pipeline.Name;
            previewProcessingEntered.TrySetResult();
            await releasePreviewProcessing.Task.WaitAsync(cancellationToken);
            await foreach (var row in source.ReadAsync(cancellationToken))
            {
                capturedSource = Assert.IsType<string>(row.Values["Content"]);
            }
            return new PreviewResult([]);
        });
        var controller = Controller(pipelineService, store, coordinator, previewService);

        var preview = controller.Preview(pipelineId, CancellationToken.None);
        await previewProcessingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var commit = coordinator.CommitAsync(
            pipelineId,
            replacementReferenceId,
            _ =>
            {
                pipelineService.Current = replacement;
                return Task.FromResult(true);
            },
            CancellationToken.None);
        Assert.Equal(
            PipelineSourceCommitStatus.Succeeded,
            await commit.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(preview.IsCompleted);
        Assert.Equal(1, store.ActiveLeaseCount);

        releasePreviewProcessing.TrySetResult();
        Assert.IsType<ViewResult>(await preview);

        Assert.Equal("S0", capturedPipeline);
        Assert.Equal("F0", capturedSource);
        Assert.Equal(0, store.ActiveLeaseCount);
    }

    [Fact]
    public async Task Preview_WaitsForActivationFailureCleanupAndCannotAcquirePriorSource()
    {
        var pipelineId = Guid.NewGuid();
        var initial = Pipeline(pipelineId, "S0");
        var replacement = Pipeline(pipelineId, "S1");
        var pipelineService = new MutablePipelineService(initial);
        var store = new CoordinatedSourceStore { ActivationSucceeds = false };
        store.SeedActive(pipelineId, "same.csv", initial.SourceOptions, "F0");
        var replacementReferenceId = store.Stage(
            pipelineId,
            "same.csv",
            replacement.SourceOptions,
            "F1");
        var discardEntered = CompletionSource();
        var releaseDiscard = CompletionSource();
        store.BeforeDiscardAsync = async cancellationToken =>
        {
            discardEntered.TrySetResult();
            await releaseDiscard.Task.WaitAsync(cancellationToken);
        };
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var previewService = new RecordingPreviewService(new PreviewResult([]));
        var controller = Controller(pipelineService, store, coordinator, previewService);

        var commit = coordinator.CommitAsync(
            pipelineId,
            replacementReferenceId,
            _ =>
            {
                pipelineService.Current = replacement;
                return Task.FromResult(true);
            },
            CancellationToken.None);
        await discardEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var preview = controller.Preview(pipelineId, CancellationToken.None);
        Assert.False(preview.IsCompleted);
        Assert.Equal(0, store.AcquireCallCount);

        releaseDiscard.TrySetResult();
        Assert.Equal(PipelineSourceCommitStatus.ActivationFailed, await commit);
        var result = Assert.IsType<ViewResult>(await preview);

        Assert.Equal(StatusCodes.Status410Gone, controller.Response.StatusCode);
        Assert.True(Assert.IsType<EtlTool.Web.Models.Pipelines.PipelinePreviewViewModel>(result.Model)
            .RequiresSourceUpload);
        Assert.Equal(0, previewService.CallCount);
        Assert.Equal(0, store.ActiveLeaseCount);
    }

    [Fact]
    public async Task Preview_CancellationWhileQueuedDoesNotAffectWriterOrLaterSnapshots()
    {
        var pipelineId = Guid.NewGuid();
        var initial = Pipeline(pipelineId, "S0");
        var replacement = Pipeline(pipelineId, "S1");
        var pipelineService = new MutablePipelineService(initial);
        var store = new CoordinatedSourceStore();
        store.SeedActive(pipelineId, "same.csv", initial.SourceOptions, "F0");
        var replacementReferenceId = store.Stage(
            pipelineId,
            "same.csv",
            replacement.SourceOptions,
            "F1");
        var coordinator = new PipelineSourceCommitCoordinator(store);
        var persistenceEntered = CompletionSource();
        var releasePersistence = CompletionSource();
        var previewService = new RecordingPreviewService(new PreviewResult([]));
        var controller = Controller(pipelineService, store, coordinator, previewService);

        var commit = coordinator.CommitAsync(
            pipelineId,
            replacementReferenceId,
            async cancellationToken =>
            {
                persistenceEntered.TrySetResult();
                await releasePersistence.Task.WaitAsync(cancellationToken);
                pipelineService.Current = replacement;
                return true;
            },
            CancellationToken.None);
        await persistenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var cancelledPreview = controller.Preview(pipelineId, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledPreview);
        Assert.Equal(0, store.AcquireCallCount);
        Assert.Equal(0, store.ActiveLeaseCount);

        releasePersistence.TrySetResult();
        Assert.Equal(PipelineSourceCommitStatus.Succeeded, await commit);
        Assert.IsType<ViewResult>(await controller.Preview(pipelineId, CancellationToken.None));

        Assert.Equal(1, previewService.CallCount);
        Assert.Equal(0, store.ActiveLeaseCount);
    }

    private static PipelinesController Controller(
        IPipelineService pipelineService,
        CoordinatedSourceStore store,
        PipelineSourceCommitCoordinator coordinator,
        IPreviewService previewService) =>
        new(
            pipelineService,
            wizardSourceStore: store,
            readinessService: new ReadyReadinessService(),
            previewService: previewService,
            sourceCommitCoordinator: coordinator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    private static PipelineDefinition Pipeline(Guid pipelineId, string name) => new()
    {
        Id = pipelineId,
        Name = name,
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            CultureName = "en-US",
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema = [new SourceFieldDefinition { Name = "Value" }],
        FieldMappings = [new FieldMapping { SourceField = "Value", TargetField = "value" }],
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = "value"
    };

    private static TaskCompletionSource CompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class MutablePipelineService(PipelineDefinition current) : IPipelineService
    {
        private readonly object _sync = new();
        private PipelineDefinition _current = current;

        public PipelineDefinition Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
            set
            {
                lock (_sync)
                {
                    _current = value;
                }
            }
        }

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipeline = Current;
            return Task.FromResult<PipelineDefinition?>(pipeline.Id == id ? pipeline : null);
        }

        public Task<PipelineDefinition> CreateAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            Guid id,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ReadyReadinessService : IPipelineReadinessService
    {
        public PipelineReadinessResult Evaluate(PipelineDefinition pipeline) => new([]);

        public Task<PipelineReadinessResult?> EvaluateAsync(
            Guid pipelineId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PipelineReadinessResult?>(new PipelineReadinessResult([]));
    }

    private sealed class RecordingPreviewService : IPreviewService
    {
        private readonly Func<IEtlSource, PipelineDefinition, CancellationToken, Task<PreviewResult>> _handler;

        public RecordingPreviewService(PreviewResult result)
            : this((_, _, _) => Task.FromResult(result))
        {
        }

        public RecordingPreviewService(
            Func<IEtlSource, PipelineDefinition, CancellationToken, Task<PreviewResult>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public Task<PreviewResult> PreviewAsync(
            IEtlSource source,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(source, pipeline, cancellationToken);
        }
    }

    private sealed class CoordinatedSourceStore : IWizardSourceStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, SourceEntry> _pending = [];
        private readonly Dictionary<Guid, SourceEntry> _active = [];
        private int _activeLeaseCount;

        public bool ActivationSucceeds { get; init; } = true;

        public Func<CancellationToken, Task>? BeforeDiscardAsync { get; set; }

        public int AcquireCallCount { get; private set; }

        public int ActiveLeaseCount => Volatile.Read(ref _activeLeaseCount);

        public void SeedActive(
            Guid pipelineId,
            string fileName,
            SourceOptions options,
            string content)
        {
            lock (_sync)
            {
                _active[pipelineId] = new SourceEntry(
                    pipelineId,
                    fileName,
                    CopyOptions(options),
                    Encoding.UTF8.GetBytes(content));
            }
        }

        public Guid Stage(
            Guid pipelineId,
            string fileName,
            SourceOptions options,
            string content)
        {
            var sourceReferenceId = Guid.NewGuid();
            lock (_sync)
            {
                _pending[sourceReferenceId] = new SourceEntry(
                    pipelineId,
                    fileName,
                    CopyOptions(options),
                    Encoding.UTF8.GetBytes(content));
            }

            return sourceReferenceId;
        }

        public Task<bool> ActivateAsync(
            Guid pipelineId,
            Guid sourceReferenceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ActivationSucceeds)
            {
                return Task.FromResult(false);
            }

            lock (_sync)
            {
                if (!_pending.Remove(sourceReferenceId, out var source)
                    || source.PipelineId != pipelineId)
                {
                    return Task.FromResult(false);
                }

                _active[pipelineId] = source;
                return Task.FromResult(true);
            }
        }

        public async Task DiscardAsync(
            Guid sourceReferenceId,
            CancellationToken cancellationToken)
        {
            if (BeforeDiscardAsync is not null)
            {
                await BeforeDiscardAsync(cancellationToken);
            }

            lock (_sync)
            {
                _pending.Remove(sourceReferenceId);
            }
        }

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                AcquireCallCount++;
                if (sourceType != SourceType.Csv
                    || !_active.TryGetValue(pipelineId, out var source)
                    || !OptionsMatch(source.Options, sourceOptions))
                {
                    return Task.FromResult<IWizardSourceLease?>(null);
                }

                Interlocked.Increment(ref _activeLeaseCount);
                return Task.FromResult<IWizardSourceLease?>(new Lease(this, source.Content));
            }
        }

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _active.Remove(pipelineId);
                foreach (var referenceId in _pending
                    .Where(pair => pair.Value.PipelineId == pipelineId)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    _pending.Remove(referenceId);
                }
            }

            return Task.CompletedTask;
        }

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _active.Remove(pipelineId);
            }

            return Task.CompletedTask;
        }

        private static SourceOptions CopyOptions(SourceOptions options) => new()
        {
            CultureName = options.CultureName,
            DateFormat = options.DateFormat,
            Delimiter = options.Delimiter,
            WorksheetName = options.WorksheetName,
            FirstRowIsHeader = options.FirstRowIsHeader
        };

        private static bool OptionsMatch(SourceOptions left, SourceOptions right) =>
            left.Delimiter == right.Delimiter
            && left.FirstRowIsHeader == right.FirstRowIsHeader
            && string.Equals(left.CultureName, right.CultureName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.DateFormat, right.DateFormat, StringComparison.Ordinal)
            && string.Equals(left.WorksheetName, right.WorksheetName, StringComparison.OrdinalIgnoreCase);

        private sealed record SourceEntry(
            Guid PipelineId,
            string FileName,
            SourceOptions Options,
            byte[] Content);

        private sealed class Lease(CoordinatedSourceStore owner, byte[] content) : IWizardSourceLease
        {
            private int _disposed;

            public async IAsyncEnumerable<DataRow> ReadAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.CompletedTask;
                cancellationToken.ThrowIfCancellationRequested();
                var row = new DataRow { SourceRowNumber = 1 };
                row.Values["Content"] = Encoding.UTF8.GetString(content);
                yield return row;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return ValueTask.CompletedTask;
                }

                Interlocked.Decrement(ref owner._activeLeaseCount);
                return ValueTask.CompletedTask;
            }
        }
    }
}
