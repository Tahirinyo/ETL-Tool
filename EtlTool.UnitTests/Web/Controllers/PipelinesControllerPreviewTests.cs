using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Preview;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerPreviewTests
{
    [Fact]
    public async Task Preview_UsesCurrentPipelineSourceAndExistingPreviewProjection()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        var previewService = new RecordingPreviewService(new PreviewResult([]));
        var controller = Controller(pipeline, Ready(), sourceStore, previewService);

        var result = await controller.Preview(pipeline.Id, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PipelinePreviewViewModel>(view.Model);
        Assert.True(model.HasPreview);
        Assert.Equal(0, model.ValidRowCount);
        Assert.Equal(["name"], model.Table!.Columns);
        Assert.Empty(model.Errors!.Errors);
        Assert.Equal(1, sourceStore.AcquireCallCount);
        Assert.Equal(pipeline.Id, sourceStore.PipelineId);
        Assert.Equal(SourceType.Csv, sourceStore.SourceType);
        Assert.Same(pipeline.SourceOptions, sourceStore.SourceOptions);
        Assert.Equal(1, previewService.CallCount);
        Assert.Same(pipeline, previewService.Pipeline);
        Assert.Same(sourceStore.LastLease, previewService.Source);
        Assert.True(sourceStore.LeaseDisposed);
        Assert.Equal(1, sourceStore.LeaseDisposeCallCount);
    }

    [Fact]
    public async Task Preview_NotReadyDoesNotAcquireOrInvokePreview()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        var previewService = new RecordingPreviewService(new PreviewResult([]));
        var readiness = new PipelineReadinessResult(
            [new PipelineReadinessProblem("Destination", "Destination is required.")]);
        var controller = Controller(pipeline, readiness, sourceStore, previewService);

        var result = await controller.Preview(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<PipelinePreviewViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Single(model.ReadinessProblems);
        Assert.False(model.HasPreview);
        Assert.Equal(0, sourceStore.AcquireCallCount);
        Assert.Equal(0, previewService.CallCount);
    }

    [Fact]
    public async Task Preview_MissingSourceReturnsGoneWithSafeReuploadState()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore { SourceAvailable = false };
        var previewService = new RecordingPreviewService(new PreviewResult([]));
        var controller = Controller(pipeline, Ready(), sourceStore, previewService);

        var result = await controller.Preview(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<PipelinePreviewViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(StatusCodes.Status410Gone, controller.Response.StatusCode);
        Assert.True(model.RequiresSourceUpload);
        Assert.Contains("Upload and inspect", model.FailureMessage);
        Assert.Equal(0, previewService.CallCount);
    }

    [Fact]
    public async Task Preview_SystemFailureReturnsSafeServerErrorAndDisposesLease()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        var previewService = new RecordingPreviewService(
            (_, _, _) => throw new InvalidDataException("sensitive parser detail"));
        var controller = Controller(pipeline, Ready(), sourceStore, previewService);

        var result = await controller.Preview(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<PipelinePreviewViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(StatusCodes.Status500InternalServerError, controller.Response.StatusCode);
        Assert.DoesNotContain("sensitive", model.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sourceStore.LeaseDisposed);
        Assert.Equal(1, sourceStore.LeaseDisposeCallCount);
    }

    [Fact]
    public async Task Preview_PostgreSqlAccessFailureReturnsSafeServerErrorAndDisposesLease()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        var previewService = new RecordingPreviewService(
            (_, _, _) => throw new PostgreSqlConnectionAccessException());
        var controller = Controller(pipeline, Ready(), sourceStore, previewService);

        var result = await controller.Preview(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<PipelinePreviewViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(StatusCodes.Status500InternalServerError, controller.Response.StatusCode);
        Assert.DoesNotContain("PostgreSQL", model.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sourceStore.LeaseDisposed);
    }

    [Fact]
    public async Task Preview_MongoDbAccessFailureReturnsSafeServerErrorAndDisposesLease()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        var previewService = new RecordingPreviewService(
            (_, _, _) => throw new MongoSourceAccessException());
        var controller = Controller(pipeline, Ready(), sourceStore, previewService);

        var result = await controller.Preview(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<PipelinePreviewViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(StatusCodes.Status500InternalServerError, controller.Response.StatusCode);
        Assert.DoesNotContain("MongoDB", model.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sourceStore.LeaseDisposed);
    }

    [Fact]
    public async Task Preview_MongoDbSchemaChangedFailureReturnsSafeServerErrorAndDisposesLease()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        var previewService = new RecordingPreviewService(
            (_, _, _) => throw new MongoSourceSchemaChangedException(
                MongoSourceSchemaInferenceException.Unsupported("payload", "Array")));
        var controller = Controller(pipeline, Ready(), sourceStore, previewService);

        var result = await controller.Preview(pipeline.Id, CancellationToken.None);

        var model = Assert.IsType<PipelinePreviewViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(StatusCodes.Status500InternalServerError, controller.Response.StatusCode);
        Assert.DoesNotContain("payload", model.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sourceStore.LeaseDisposed);
    }

    [Fact]
    public async Task Preview_PropagatesRequestCancellationAndDisposesLease()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        using var cancellation = new CancellationTokenSource();
        var previewService = new RecordingPreviewService(
            (_, _, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new PreviewResult([]));
            });
        var controller = Controller(pipeline, Ready(), sourceStore, previewService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.Preview(pipeline.Id, cancellation.Token));

        Assert.True(sourceStore.LeaseDisposed);
        Assert.Equal(1, sourceStore.LeaseDisposeCallCount);
    }

    [Fact]
    public async Task Edit_RejectsTamperedUpsertKeyWithoutChangingAggregate()
    {
        var pipeline = ReadyPipeline();
        var service = new StubPipelineService(pipeline);
        var controller = new PipelinesController(service);
        var model = new PipelineFormViewModel
        {
            Name = pipeline.Name,
            DestinationDatabase = "changed",
            DestinationCollection = "changed",
            UpsertKeyField = "excluded"
        };

        var result = await controller.Edit(pipeline.Id, model, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(0, service.UpdateCallCount);
        Assert.Equal("demo", pipeline.DestinationDatabase);
        Assert.Equal("rows", pipeline.DestinationCollection);
        Assert.Equal("name", pipeline.UpsertKeyField);
        Assert.Equal(["name"], model.AvailableMappedFields);
    }

    [Fact]
    public async Task InspectSource_ActivatesPendingSourceOnlyAfterPipelinePersistence()
    {
        var pipeline = ReadyPipeline();
        pipeline.FieldMappings.RemoveAll(mapping => mapping.SourceField == "Ignored");
        var pipelineService = new StubPipelineService(pipeline);
        var sourceReferenceId = Guid.NewGuid();
        var inspection = new StubInspectionService(sourceReferenceId);
        var sourceStore = new RecordingWizardSourceStore
        {
            BeforeActivate = () => Assert.Equal(1, pipelineService.UpdateCallCount)
        };
        var controller = new PipelinesController(
            pipelineService,
            inspection,
            wizardSourceStore: sourceStore,
            sourceCommitCoordinator: new PipelineSourceCommitCoordinator(sourceStore));
        var bytes = Encoding.UTF8.GetBytes("Name\nAda");
        var file = new FormFile(
            new MemoryStream(bytes),
            0,
            bytes.Length,
            "SourceFile",
            "source.csv");

        var result = await controller.InspectSource(
            pipeline.Id,
            new SourceUploadViewModel
            {
                SourceFile = file,
                SourceType = SourceType.Csv,
                Delimiter = CsvDelimiter.Comma
            },
            CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(1, sourceStore.ActivateCallCount);
        Assert.Equal(pipeline.Id, sourceStore.ActivatedPipelineId);
        Assert.Equal(sourceReferenceId, sourceStore.ActivatedSourceReferenceId);
        Assert.Equal(0, sourceStore.DiscardCallCount);
    }

    [Fact]
    public async Task InspectSource_FailedPipelinePersistenceDiscardsPendingSourceWithoutActivation()
    {
        var pipeline = ReadyPipeline();
        pipeline.FieldMappings.RemoveAll(mapping => mapping.SourceField == "Ignored");
        var pipelineService = new StubPipelineService(pipeline, updateResult: false);
        var sourceReferenceId = Guid.NewGuid();
        var sourceStore = new RecordingWizardSourceStore();
        var controller = new PipelinesController(
            pipelineService,
            new StubInspectionService(sourceReferenceId),
            wizardSourceStore: sourceStore,
            sourceCommitCoordinator: new PipelineSourceCommitCoordinator(sourceStore));
        var bytes = Encoding.UTF8.GetBytes("Name\nAda");
        var file = new FormFile(
            new MemoryStream(bytes),
            0,
            bytes.Length,
            "SourceFile",
            "source.csv");

        var result = await controller.InspectSource(
            pipeline.Id,
            new SourceUploadViewModel
            {
                SourceFile = file,
                SourceType = SourceType.Csv,
                Delimiter = CsvDelimiter.Comma
            },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, sourceStore.ActivateCallCount);
        Assert.Equal(1, sourceStore.DiscardCallCount);
        Assert.Equal(sourceReferenceId, sourceStore.DiscardedSourceReferenceId);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesRetainedSourceAfterPipelineDeletion()
    {
        var pipeline = ReadyPipeline();
        var sourceStore = new RecordingWizardSourceStore();
        var controller = new PipelinesController(
            new StubPipelineService(pipeline),
            wizardSourceStore: sourceStore);

        var result = await controller.DeleteConfirmed(pipeline.Id, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(1, sourceStore.RemoveCallCount);
        Assert.Equal(pipeline.Id, sourceStore.RemovedPipelineId);
    }

    private static PipelinesController Controller(
        PipelineDefinition pipeline,
        PipelineReadinessResult readiness,
        RecordingWizardSourceStore sourceStore,
        RecordingPreviewService previewService)
    {
        var controller = new PipelinesController(
            new StubPipelineService(pipeline),
            wizardSourceStore: sourceStore,
            readinessService: new StubReadinessService(readiness),
            previewService: previewService,
            sourceCommitCoordinator: new PipelineSourceCommitCoordinator(sourceStore))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private static PipelineReadinessResult Ready() => new([]);

    private static PipelineDefinition ReadyPipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Import",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            CultureName = "tr-TR",
            DateFormat = "dd.MM.yyyy",
            Delimiter = CsvDelimiter.Semicolon,
            FirstRowIsHeader = true
        },
        ExpectedSchema = [new SourceFieldDefinition { Name = "Name" }],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Name", TargetField = "name" },
            new FieldMapping { SourceField = "Ignored", TargetField = "excluded", IsIncluded = false }
        ],
        DestinationDatabase = "demo",
        DestinationCollection = "rows",
        UpsertKeyField = "name"
    };

    private sealed class StubPipelineService(
        PipelineDefinition pipeline,
        bool updateResult = true,
        bool deleteResult = true) : IPipelineService
    {
        public int UpdateCallCount { get; private set; }

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            return Task.FromResult(updateResult);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(deleteResult && id == pipeline.Id);
    }

    private sealed class StubReadinessService(PipelineReadinessResult result) : IPipelineReadinessService
    {
        public PipelineReadinessResult Evaluate(PipelineDefinition pipeline) => result;

        public Task<PipelineReadinessResult?> EvaluateAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineReadinessResult?>(result);
    }

    private sealed class RecordingWizardSourceStore : IWizardSourceStore
    {
        public IWizardSourceLease? LastLease { get; private set; }
        public bool SourceAvailable { get; init; } = true;
        public int AcquireCallCount { get; private set; }
        public Guid PipelineId { get; private set; }
        public SourceType SourceType { get; private set; }
        public SourceOptions? SourceOptions { get; private set; }
        public bool LeaseDisposed { get; private set; }
        public int LeaseDisposeCallCount { get; private set; }
        public int ActivateCallCount { get; private set; }
        public int DiscardCallCount { get; private set; }
        public Guid ActivatedPipelineId { get; private set; }
        public Guid ActivatedSourceReferenceId { get; private set; }
        public Guid DiscardedSourceReferenceId { get; private set; }
        public int RemoveCallCount { get; private set; }
        public Guid RemovedPipelineId { get; private set; }
        public Action? BeforeActivate { get; init; }

        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken)
        {
            BeforeActivate?.Invoke();
            ActivateCallCount++;
            ActivatedPipelineId = pipelineId;
            ActivatedSourceReferenceId = sourceReferenceId;
            return Task.FromResult(true);
        }

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken)
        {
            DiscardCallCount++;
            DiscardedSourceReferenceId = sourceReferenceId;
            return Task.CompletedTask;
        }

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            AcquireCallCount++;
            PipelineId = pipelineId;
            SourceType = sourceType;
            SourceOptions = sourceOptions;
            LastLease = SourceAvailable ? new Lease(this) : null;
            return Task.FromResult(LastLease);
        }

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            RemoveCallCount++;
            RemovedPipelineId = pipelineId;
            return Task.CompletedTask;
        }

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        private sealed class Lease(RecordingWizardSourceStore owner) : IWizardSourceLease
        {
            public async IAsyncEnumerable<DataRow> ReadAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.CompletedTask;
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }

            public ValueTask DisposeAsync()
            {
                owner.LeaseDisposed = true;
                owner.LeaseDisposeCallCount++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubInspectionService(Guid sourceReferenceId) : ISourceInspectionService
    {
        public Task<SourceInspectionResult> InspectCsvAsync(
            Guid pipelineId,
            Stream content,
            string fileName,
            SourceOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SourceInspectionResult
            {
                SourceType = SourceType.Csv,
                Columns = ["Name"],
                DetectedSchema = [new SourceFieldDefinition { Name = "Name" }],
                SourceReferenceId = sourceReferenceId
            });

        public Task<SourceInspectionResult> StageXlsxAsync(
            Guid pipelineId,
            Stream content,
            string fileName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SourceInspectionResult> InspectStagedXlsxAsync(
            Guid pipelineId,
            Guid stageId,
            string worksheetName,
            CancellationToken cancellationToken,
            SourceOptions? sourceOptions = null) =>
            throw new NotSupportedException();
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
        public IEtlSource? Source { get; private set; }
        public PipelineDefinition? Pipeline { get; private set; }

        public Task<PreviewResult> PreviewAsync(
            IEtlSource source,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Source = source;
            Pipeline = pipeline;
            return _handler(source, pipeline, cancellationToken);
        }
    }
}
