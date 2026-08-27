using System.Text;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerSchemaRemappingTests
{
    [Fact]
    public async Task InspectSource_AddedFieldPersistsExcludedMappingAndPreservesExistingMapping()
    {
        var pipeline = Pipeline();
        var service = new RecordingPipelineService(pipeline);
        var source = new RecordingSourceInspectionService
        {
            CsvResult = Inspection(
                [Field("Id", SourceFieldType.Integer), Field("Legacy", SourceFieldType.String), Field("Name", SourceFieldType.String)])
        };
        var controller = new PipelinesController(service, source);
        var file = new FormFile(
            new MemoryStream(Encoding.UTF8.GetBytes("Id,Name\n1,Ada")),
            0,
            13,
            "SourceFile",
            "customers.csv");

        var result = await controller.InspectSource(pipeline.Id, new SourceUploadViewModel
        {
            SourceType = SourceType.Csv,
            Delimiter = CsvDelimiter.Comma,
            SourceFile = file
        }, CancellationToken.None);

        var model = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Single(model.SchemaDifference!.NewFields);
        Assert.Equal("Name", model.SchemaDifference.NewFields[0].Name);
        Assert.Equal(1, service.UpdateCallCount);
        Assert.Collection(service.UpdatedPipeline!.FieldMappings,
            mapping =>
            {
                Assert.Equal("Id", mapping.SourceField);
                Assert.Equal("id", mapping.TargetField);
                Assert.True(mapping.IsIncluded);
            },
            mapping =>
            {
                Assert.Equal("Legacy", mapping.SourceField);
                Assert.Equal("legacy", mapping.TargetField);
                Assert.True(mapping.IsIncluded);
            },
            mapping =>
            {
                Assert.Equal("Name", mapping.SourceField);
                Assert.Equal(string.Empty, mapping.TargetField);
                Assert.False(mapping.IsIncluded);
            });
        Assert.Equal("Legacy", pipeline.ExpectedSchema[1].Name);
    }

    [Fact]
    public async Task InspectSource_RemovedMappedFieldKeepsPipelineUnchangedAndOffersRepair()
    {
        var pipeline = Pipeline();
        var sourceReferenceId = Guid.NewGuid();
        var service = new RecordingPipelineService(pipeline);
        var source = new RecordingSourceInspectionService
        {
            CsvResult = new SourceInspectionResult
            {
                SourceType = SourceType.Csv,
                Columns = ["Id", "Replacement"],
                DetectedSchema = [Field("Id", SourceFieldType.Integer), Field("Replacement", SourceFieldType.String)],
                SourceReferenceId = sourceReferenceId
            }
        };
        var controller = new PipelinesController(service, source);
        var file = new FormFile(
            new MemoryStream(Encoding.UTF8.GetBytes("Id,Replacement\n1,Ada")),
            0,
            20,
            "SourceFile",
            "customers.csv");

        var result = await controller.InspectSource(pipeline.Id, new SourceUploadViewModel
        {
            SourceType = SourceType.Csv,
            Delimiter = CsvDelimiter.Comma,
            SourceFile = file
        }, CancellationToken.None);

        var model = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(sourceReferenceId, model.PendingSourceReferenceId);
        Assert.True(model.SchemaDifference!.RequiresRemapping);
        Assert.Equal("Legacy", Assert.Single(model.SchemaDifference.MissingFields).Name);
        Assert.Equal("Legacy", Assert.Single(model.SchemaDifference.UnresolvedMappings).SourceField);
        Assert.Equal(0, service.UpdateCallCount);
        Assert.Equal(["Id", "Legacy"], pipeline.ExpectedSchema.Select(field => field.Name));
    }

    [Fact]
    public async Task InspectSource_ReorderedAndTypeChangedSchemaPreservesMappingIdentity()
    {
        var pipeline = Pipeline();
        var service = new RecordingPipelineService(pipeline);
        var controller = new PipelinesController(service, new RecordingSourceInspectionService
        {
            CsvResult = Inspection(
                [Field("Legacy", SourceFieldType.Integer), Field("Id", SourceFieldType.Integer)])
        });
        var file = new FormFile(
            new MemoryStream(Encoding.UTF8.GetBytes("Legacy,Id\n1,2")),
            0,
            13,
            "SourceFile",
            "customers.csv");

        var result = await controller.InspectSource(pipeline.Id, new SourceUploadViewModel
        {
            SourceType = SourceType.Csv,
            Delimiter = CsvDelimiter.Comma,
            SourceFile = file
        }, CancellationToken.None);

        var model = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Single(model.SchemaDifference!.TypeChanges);
        Assert.Empty(model.SchemaDifference.MissingFields);
        Assert.Empty(model.SchemaDifference.NewFields);
        Assert.Collection(service.UpdatedPipeline!.FieldMappings,
            mapping =>
            {
                Assert.Equal("Id", mapping.SourceField);
                Assert.Equal("id", mapping.TargetField);
            },
            mapping =>
            {
                Assert.Equal("Legacy", mapping.SourceField);
                Assert.Equal("legacy", mapping.TargetField);
            });
        Assert.Equal(["Legacy", "Id"], service.UpdatedPipeline.ExpectedSchema.Select(field => field.Name));
    }

    [Fact]
    public async Task Remapping_PreservesResolvableMappingsAndCommitsOnlyAfterValidRepair()
    {
        var pipeline = Pipeline();
        var sourceReferenceId = Guid.NewGuid();
        var pending = new PendingSourceInspection
        {
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions { Delimiter = CsvDelimiter.Semicolon, CultureName = "tr-TR" },
            DetectedSchema = [Field("Id", SourceFieldType.Integer), Field("Replacement", SourceFieldType.String)]
        };
        var pipelineService = new RecordingPipelineService(pipeline);
        var inspection = new RecordingSourceInspectionService { Pending = pending };
        var sourceStore = new RecordingSourceStore();
        var controller = new PipelinesController(
            pipelineService,
            inspection,
            sourceCommitCoordinator: new PipelineSourceCommitCoordinator(sourceStore));

        var getResult = await controller.Mapping(pipeline.Id, CancellationToken.None, sourceReferenceId);

        var getModel = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(getResult).Model);
        Assert.Equal(sourceReferenceId, getModel.PendingSourceReferenceId);
        Assert.Collection(getModel.Fields,
            field =>
            {
                Assert.Equal("Id", field.SourceField);
                Assert.Equal("id", field.TargetField);
                Assert.True(field.IsIncluded);
            },
            field =>
            {
                Assert.Equal("Replacement", field.SourceField);
                Assert.Equal(string.Empty, field.TargetField);
                Assert.False(field.IsIncluded);
            });
        Assert.Equal("Legacy", Assert.Single(getModel.SchemaDifference!.UnresolvedMappings).SourceField);
        Assert.Equal(0, pipelineService.UpdateCallCount);
        Assert.Equal("Legacy", pipeline.ExpectedSchema[1].Name);

        var postResult = await controller.Mapping(pipeline.Id, new FieldMappingViewModel
        {
            PendingSourceReferenceId = sourceReferenceId,
            Fields =
            [
                new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "id", IsIncluded = true },
                new FieldMappingFieldViewModel { SourceField = "Replacement", TargetField = "legacy", IsIncluded = true }
            ]
        }, CancellationToken.None);

        Assert.True(Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(postResult).Model).IsSaved);
        Assert.Equal(1, pipelineService.UpdateCallCount);
        Assert.Equal(["Id", "Replacement"], pipelineService.UpdatedPipeline!.ExpectedSchema.Select(field => field.Name));
        Assert.Equal(CsvDelimiter.Semicolon, pipelineService.UpdatedPipeline.SourceOptions.Delimiter);
        Assert.Equal(sourceReferenceId, sourceStore.ActivatedSourceReferenceId);
        Assert.Equal("Legacy", pipeline.ExpectedSchema[1].Name);

        var revisit = await controller.Mapping(pipeline.Id, CancellationToken.None);
        var revisitModel = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(revisit).Model);
        Assert.Null(revisitModel.PendingSourceReferenceId);
        Assert.Null(revisitModel.SchemaDifference);
        Assert.Equal(["Id", "Replacement"], revisitModel.Fields.Select(field => field.SourceField));
    }

    [Fact]
    public async Task Remapping_ActivationFailureRestoresCompletePipelineAndPreservesPriorSource()
    {
        var pipeline = Pipeline();
        pipeline.Description = "Prior configuration";
        pipeline.SourceOptions.CultureName = "en-US";
        pipeline.TransformationRules =
        [
            new TransformationRule
            {
                Id = Guid.NewGuid(),
                Type = TransformationType.Trim,
                Order = 1,
                SourceField = "legacy",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Mode"] = "Prior"
                }
            }
        ];
        pipeline.ValidationRules =
        [
            new ValidationRule
            {
                Id = Guid.NewGuid(),
                Type = ValidationType.Required,
                Field = "legacy",
                ErrorMessage = "Required",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Mode"] = "Prior"
                }
            }
        ];
        pipeline.DestinationDatabase = "etl";
        pipeline.DestinationCollection = "customers";
        pipeline.UpsertKeyField = "id";
        var priorSourceReferenceId = Guid.NewGuid();
        var pendingSourceReferenceId = Guid.NewGuid();
        var pipelineService = new RecordingPipelineService(pipeline);
        var sourceStore = new RecordingSourceStore
        {
            ActivationSucceeds = false,
            ActivePipelineId = pipeline.Id,
            ActiveSourceReferenceId = priorSourceReferenceId,
            ActiveContent = Encoding.UTF8.GetBytes("Id,Legacy\n1,prior")
        };
        var controller = new PipelinesController(
            pipelineService,
            new RecordingSourceInspectionService
            {
                Pending = new PendingSourceInspection
                {
                    SourceType = SourceType.Xlsx,
                    SourceOptions = new SourceOptions
                    {
                        WorksheetName = "Replacement",
                        CultureName = "tr-TR"
                    },
                    DetectedSchema =
                    [
                        Field("Id", SourceFieldType.Integer),
                        Field("Replacement", SourceFieldType.String)
                    ]
                }
            },
            sourceCommitCoordinator: new PipelineSourceCommitCoordinator(sourceStore));

        var result = await controller.Mapping(pipeline.Id, new FieldMappingViewModel
        {
            PendingSourceReferenceId = pendingSourceReferenceId,
            Fields =
            [
                new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "id", IsIncluded = true },
                new FieldMappingFieldViewModel { SourceField = "Replacement", TargetField = "legacy", IsIncluded = true }
            ]
        }, CancellationToken.None);

        var model = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.False(model.IsSaved);
        Assert.Contains(
            controller.ModelState[string.Empty]!.Errors,
            error => error.ErrorMessage.Contains("Upload the source again", StringComparison.Ordinal));
        Assert.Equal(2, pipelineService.UpdateCallCount);
        var restored = pipelineService.CurrentPipeline;
        Assert.Equal(SourceType.Csv, restored.SourceType);
        Assert.Equal(CsvDelimiter.Comma, restored.SourceOptions.Delimiter);
        Assert.Equal("en-US", restored.SourceOptions.CultureName);
        Assert.Equal(["Id", "Legacy"], restored.ExpectedSchema.Select(field => field.Name));
        Assert.Equal([("Id", "id"), ("Legacy", "legacy")],
            restored.FieldMappings.Select(mapping => (mapping.SourceField, mapping.TargetField)));
        Assert.Equal("Prior", Assert.Single(restored.TransformationRules).Configuration["Mode"]);
        Assert.Equal("Prior", Assert.Single(restored.ValidationRules).Configuration["Mode"]);
        Assert.Equal("etl", restored.DestinationDatabase);
        Assert.Equal("customers", restored.DestinationCollection);
        Assert.Equal("id", restored.UpsertKeyField);
        Assert.NotSame(pipeline.SourceOptions, restored.SourceOptions);
        Assert.NotSame(pipeline.TransformationRules[0].Configuration,
            restored.TransformationRules[0].Configuration);
        Assert.Equal(pendingSourceReferenceId, sourceStore.ActivatedSourceReferenceId);
        Assert.Equal(priorSourceReferenceId, sourceStore.ActiveSourceReferenceId);
        Assert.Contains(pendingSourceReferenceId, sourceStore.DiscardedSourceReferenceIds);
        Assert.Empty(sourceStore.RetiredPipelineIds);

        await using var lease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await sourceStore.AcquireAsync(
                pipeline.Id,
                restored.SourceType,
                restored.SourceOptions,
                CancellationToken.None));
        using var reader = new StreamReader(lease.Content, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        Assert.Equal("1,prior", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Remapping_CrossPipelinePendingSourceReturnsUploadPathWithoutPersisting()
    {
        var pipeline = Pipeline();
        var service = new RecordingPipelineService(pipeline);
        var controller = new PipelinesController(
            service,
            new RecordingSourceInspectionService { PendingOwner = Guid.NewGuid(), Pending = new PendingSourceInspection
            {
                SourceType = SourceType.Csv,
                SourceOptions = new SourceOptions(),
                DetectedSchema = [Field("Id", SourceFieldType.Integer)]
            }});

        var result = await controller.Mapping(pipeline.Id, CancellationToken.None, Guid.NewGuid());

        Assert.Equal("Source", Assert.IsType<ViewResult>(result).ViewName);
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task Remapping_TamperedSubmittedSchemaIsRejectedWithoutPersisting()
    {
        var pipeline = Pipeline();
        var service = new RecordingPipelineService(pipeline);
        var controller = new PipelinesController(
            service,
            new RecordingSourceInspectionService
            {
                Pending = new PendingSourceInspection
                {
                    SourceType = SourceType.Csv,
                    SourceOptions = new SourceOptions(),
                    DetectedSchema = [Field("Id", SourceFieldType.Integer), Field("Replacement", SourceFieldType.String)]
                }
            });

        var result = await controller.Mapping(pipeline.Id, new FieldMappingViewModel
        {
            PendingSourceReferenceId = Guid.NewGuid(),
            Fields =
            [
                new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "id" },
                new FieldMappingFieldViewModel { SourceField = "Unexpected", TargetField = "legacy" }
            ]
        }, CancellationToken.None);

        var model = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(["Id", "Replacement"], model.Fields.Select(field => field.SourceField));
        Assert.Equal(0, service.UpdateCallCount);
    }

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Import",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions { Delimiter = CsvDelimiter.Comma },
        ExpectedSchema = [Field("Id", SourceFieldType.Integer), Field("Legacy", SourceFieldType.String)],
        FieldMappings = [Mapping("Id", "id"), Mapping("Legacy", "legacy")]
    };

    private static SourceInspectionResult Inspection(IReadOnlyList<SourceFieldDefinition> schema) => new()
    {
        SourceType = SourceType.Csv,
        Columns = schema.Select(field => field.Name).ToArray(),
        DetectedSchema = schema,
        SourceReferenceId = Guid.NewGuid()
    };

    private static SourceFieldDefinition Field(string name, SourceFieldType type) => new()
    {
        Name = name,
        DataType = type
    };

    private static FieldMapping Mapping(string source, string target) => new()
    {
        SourceField = source,
        TargetField = target,
        IsIncluded = true
    };

    private sealed class RecordingPipelineService(PipelineDefinition pipeline) : IPipelineService
    {
        private PipelineDefinition _pipeline = pipeline;

        public int UpdateCallCount { get; private set; }
        public PipelineDefinition? UpdatedPipeline { get; private set; }

        public PipelineDefinition CurrentPipeline => _pipeline;

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == _pipeline.Id ? _pipeline : null);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            UpdatedPipeline = value;
            if (id != _pipeline.Id)
            {
                return Task.FromResult(false);
            }

            _pipeline = value;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSourceInspectionService : ISourceInspectionService
    {
        public SourceInspectionResult? CsvResult { get; init; }
        public PendingSourceInspection? Pending { get; init; }
        public Guid? PendingOwner { get; init; }

        public Task<SourceInspectionResult> InspectCsvAsync(
            Guid pipelineId,
            Stream content,
            string fileName,
            SourceOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult(CsvResult ?? throw new InvalidOperationException());

        public Task<SourceInspectionResult> StageXlsxAsync(
            Guid pipelineId,
            Stream content,
            string fileName,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SourceInspectionResult> InspectStagedXlsxAsync(
            Guid pipelineId,
            Guid stageId,
            string worksheetName,
            CancellationToken cancellationToken,
            SourceOptions? sourceOptions = null) => throw new NotSupportedException();

        public Task<PendingSourceInspection?> GetPendingSourceAsync(
            Guid pipelineId,
            Guid sourceReferenceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PendingOwner is null || PendingOwner == pipelineId ? Pending : null);
    }

    private sealed class RecordingSourceStore : IWizardSourceStore
    {
        public Guid? ActivatedSourceReferenceId { get; private set; }

        public bool ActivationSucceeds { get; init; } = true;

        public Guid? ActivePipelineId { get; init; }

        public Guid? ActiveSourceReferenceId { get; set; }

        public byte[] ActiveContent { get; init; } = [];

        public List<Guid> DiscardedSourceReferenceIds { get; } = [];

        public List<Guid> RetiredPipelineIds { get; } = [];

        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken)
        {
            ActivatedSourceReferenceId = sourceReferenceId;
            if (ActivationSucceeds)
            {
                ActiveSourceReferenceId = sourceReferenceId;
            }

            return Task.FromResult(ActivationSucceeds);
        }

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken)
        {
            DiscardedSourceReferenceIds.Add(sourceReferenceId);
            return Task.CompletedTask;
        }

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IWizardSourceLease?>(
                pipelineId == ActivePipelineId && ActiveSourceReferenceId is not null
                    ? new MemorySourceLease(ActiveContent)
                    : null);

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            RetiredPipelineIds.Add(pipelineId);
            ActiveSourceReferenceId = null;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySourceLease(byte[] content) : IWizardSourceLease
    {
        public Stream Content { get; } = new MemoryStream(content, writable: false);

        public async ValueTask DisposeAsync() => await Content.DisposeAsync();
    }
}
