using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Application.Execution;
using EtlTool.Application.Sources;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

public sealed class PipelinesController : Controller
{
    private readonly IPipelineService _pipelineService;
    private readonly ISourceInspectionService? _sourceInspectionService;
    private readonly FieldMappingService _fieldMappingService;
    private readonly IWizardSourceStore? _wizardSourceStore;
    private readonly IPipelineReadinessService? _readinessService;
    private readonly IPreviewService? _previewService;
    private readonly ILogger<PipelinesController>? _logger;
    private readonly PipelineSourceCommitCoordinator? _sourceCommitCoordinator;
    private readonly IMongoTargetAccessService? _targetAccessService;
    private readonly SourceSchemaComparisonService _schemaComparisonService;
    private readonly IRunAdmissionService? _runAdmissionService;
    private readonly IPostgreSqlMetadataDiscoveryService? _postgreSqlMetadataDiscoveryService;
    private readonly IPostgreSqlConnectionProfileCatalog? _postgreSqlConnectionProfileCatalog;
    private readonly PostgreSqlSourceSchemaConverter _postgreSqlSourceSchemaConverter;
    private readonly IMongoSourceMetadataDiscoveryService? _mongoSourceMetadataDiscoveryService;
    private readonly IMongoSourceSchemaInferenceService? _mongoSourceSchemaInferenceService;

    public PipelinesController(
        IPipelineService pipelineService,
        ISourceInspectionService? sourceInspectionService = null,
        FieldMappingService? fieldMappingService = null,
        IWizardSourceStore? wizardSourceStore = null,
        IPipelineReadinessService? readinessService = null,
        IPreviewService? previewService = null,
        ILogger<PipelinesController>? logger = null,
        PipelineSourceCommitCoordinator? sourceCommitCoordinator = null,
        IMongoTargetAccessService? targetAccessService = null,
        SourceSchemaComparisonService? schemaComparisonService = null,
        IRunAdmissionService? runAdmissionService = null,
        IPostgreSqlMetadataDiscoveryService? postgreSqlMetadataDiscoveryService = null,
        IPostgreSqlConnectionProfileCatalog? postgreSqlConnectionProfileCatalog = null,
        PostgreSqlSourceSchemaConverter? postgreSqlSourceSchemaConverter = null,
        IMongoSourceMetadataDiscoveryService? mongoSourceMetadataDiscoveryService = null,
        IMongoSourceSchemaInferenceService? mongoSourceSchemaInferenceService = null)
    {
        ArgumentNullException.ThrowIfNull(pipelineService);
        _pipelineService = pipelineService;
        _sourceInspectionService = sourceInspectionService;
        _fieldMappingService = fieldMappingService ?? new FieldMappingService();
        _wizardSourceStore = wizardSourceStore;
        _readinessService = readinessService;
        _previewService = previewService;
        _logger = logger;
        _sourceCommitCoordinator = sourceCommitCoordinator;
        _targetAccessService = targetAccessService;
        _schemaComparisonService = schemaComparisonService ?? new SourceSchemaComparisonService();
        _runAdmissionService = runAdmissionService;
        _postgreSqlMetadataDiscoveryService = postgreSqlMetadataDiscoveryService;
        _postgreSqlConnectionProfileCatalog = postgreSqlConnectionProfileCatalog;
        _postgreSqlSourceSchemaConverter = postgreSqlSourceSchemaConverter ?? new PostgreSqlSourceSchemaConverter();
        _mongoSourceMetadataDiscoveryService = mongoSourceMetadataDiscoveryService;
        _mongoSourceSchemaInferenceService = mongoSourceSchemaInferenceService;
    }

    public async Task<IActionResult> Mapping(
        Guid id,
        CancellationToken cancellationToken,
        Guid? pendingSourceReferenceId = null)
    {
        if (id == Guid.Empty) return NotFound();

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();

        if (pendingSourceReferenceId is Guid sourceReferenceId)
        {
            return await CreateRemappingViewAsync(
                pipeline,
                sourceReferenceId,
                cancellationToken);
        }

        if (pipeline.ExpectedSchema.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Inspect a source schema before configuring fields.");
            return View(new FieldMappingViewModel());
        }

        var model = CreateMappingModel(pipeline, out var mappingError);
        if (mappingError is not null)
        {
            ModelState.AddModelError(string.Empty, mappingError);
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Mapping(
        [FromRoute] Guid id,
        FieldMappingViewModel model,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return NotFound();

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();

        if (model.PendingSourceReferenceId is Guid sourceReferenceId)
        {
            return await SaveRemappingAsync(
                id,
                pipeline,
                sourceReferenceId,
                model,
                cancellationToken);
        }

        if (pipeline.ExpectedSchema.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Inspect a source schema before configuring fields.");
            return View(model);
        }

        if (!MatchesExpectedSchema(model.Fields, pipeline.ExpectedSchema))
        {
            ModelState.Clear();
            ModelState.AddModelError(string.Empty, "The submitted mapping fields do not match the inspected source schema. Reload the page and try again.");
            return View(CreateMappingModel(pipeline, out _));
        }

        ApplyTrustedFieldTypes(model, pipeline.ExpectedSchema);

        if (!ModelState.IsValid) return View(model);

        var fieldMappings = model.Fields
            .Select(field => new FieldMapping
            {
                SourceField = field.SourceField,
                TargetField = field.TargetField ?? string.Empty,
                IsIncluded = field.IsIncluded
            })
            .ToList();

        var configuration = new PipelineDefinition
        {
            ExpectedSchema = CopySchema(pipeline.ExpectedSchema),
            FieldMappings = fieldMappings
        };

        try
        {
            _fieldMappingService.Prepare(configuration);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return View(model);
        }

        var replacement = CopyPipelineWithMappings(pipeline, fieldMappings);
        replacement.RequiresRemapping = false;
        if (!await _pipelineService.UpdateAsync(id, replacement, cancellationToken))
        {
            return NotFound();
        }

        model.IsSaved = true;
        return View(model);
    }

    public async Task<IActionResult> Source(Guid id, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        return View(CreateSourceModel(pipeline));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InspectSource(
        [FromRoute] Guid id,
        SourceUploadViewModel model,
        CancellationToken cancellationToken,
        string? postgreSqlAction = null,
        string? mongoDbAction = null)
    {
        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        if (model.SourceType == SourceType.PostgreSql)
        {
            return await ConfigurePostgreSqlSourceAsync(
                id,
                pipeline,
                model,
                postgreSqlAction,
                cancellationToken);
        }
        if (model.SourceType == SourceType.MongoDb)
        {
            return await ConfigureMongoDbSourceAsync(
                id,
                pipeline,
                model,
                mongoDbAction,
                cancellationToken);
        }
        if (_sourceInspectionService is null) throw new InvalidOperationException("Source inspection is not configured.");
        if (model.SourceFile is null || model.SourceFile.Length == 0)
        {
            ModelState.AddModelError(nameof(model.SourceFile), "Choose a non-empty CSV or XLSX file.");
            return View("Source", model);
        }
        if (model.SourceType is not SourceType.Csv and not SourceType.Xlsx)
        {
            ModelState.AddModelError(nameof(model.SourceType), "Choose CSV or XLSX.");
            return View("Source", model);
        }
        if (model.SourceType == SourceType.Csv && !IsSupportedDelimiter(model.Delimiter))
        {
            ModelState.AddModelError(nameof(model.Delimiter), "Choose comma, semicolon, or tab as the CSV delimiter.");
            return View("Source", model);
        }
        await using var content = model.SourceFile.OpenReadStream();
        if (model.SourceType == SourceType.Xlsx)
        {
            var stage = await _sourceInspectionService.StageXlsxAsync(id, content, model.SourceFile.FileName, cancellationToken);
            return ApplyInspection(model, stage);
        }
        var options = CreateCsvOptions(pipeline, model.Delimiter);
        var inspection = await _sourceInspectionService.InspectCsvAsync(
            id,
            content,
            model.SourceFile.FileName,
            options,
            cancellationToken);
        var comparison = inspection.IsSuccess && pipeline.ExpectedSchema.Count > 0
            ? _schemaComparisonService.Compare(
                pipeline.ExpectedSchema,
                inspection.DetectedSchema,
                pipeline.FieldMappings ?? [])
            : null;
        var result = ApplyInspection(model, inspection, comparison);
        if (inspection.IsSuccess)
        {
            if (RequiresExplicitSchemaConfirmation(comparison))
            {
                model.PendingSourceReferenceId = inspection.SourceReferenceId;
                return result;
            }

            var commitStatus = await CommitSourceAsync(
                id,
                pipeline,
                SourceType.Csv,
                options,
                inspection,
                ReconcileMappings(pipeline, inspection.DetectedSchema),
                cancellationToken);
            if (commitStatus == PipelineSourceCommitStatus.PersistenceRejected)
            {
                return NotFound();
            }

            AddActivationFailure(commitStatus);
        }

        return result;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectWorksheet([FromRoute] Guid id, SourceUploadViewModel model, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        if (_sourceInspectionService is null) throw new InvalidOperationException("Source inspection is not configured.");
        if (model.StageId is not Guid stageId || string.IsNullOrWhiteSpace(model.WorksheetName))
        {
            ModelState.AddModelError(nameof(model.WorksheetName), "Choose a worksheet.");
            return View("Source", model);
        }
        var options = new SourceOptions
        {
            WorksheetName = model.WorksheetName,
            FirstRowIsHeader = true,
            CultureName = pipeline.SourceOptions.CultureName,
            DateFormat = pipeline.SourceOptions.DateFormat
        };
        var inspection = await _sourceInspectionService.InspectStagedXlsxAsync(
            id,
            stageId,
            model.WorksheetName,
            cancellationToken,
            options);
        var comparison = inspection.IsSuccess && pipeline.ExpectedSchema.Count > 0
            ? _schemaComparisonService.Compare(
                pipeline.ExpectedSchema,
                inspection.DetectedSchema,
                pipeline.FieldMappings ?? [])
            : null;
        var result = ApplyInspection(model, inspection, comparison);
        if (inspection.IsSuccess)
        {
            if (RequiresExplicitSchemaConfirmation(comparison))
            {
                model.PendingSourceReferenceId = inspection.SourceReferenceId;
                return result;
            }

            var commitStatus = await CommitSourceAsync(
                id,
                pipeline,
                SourceType.Xlsx,
                options,
                inspection,
                ReconcileMappings(pipeline, inspection.DetectedSchema),
                cancellationToken);
            if (commitStatus == PipelineSourceCommitStatus.PersistenceRejected)
            {
                return NotFound();
            }

            AddActivationFailure(commitStatus);
        }

        return result;
    }

    private async Task<IActionResult> CreateRemappingViewAsync(
        PipelineDefinition pipeline,
        Guid sourceReferenceId,
        CancellationToken cancellationToken)
    {
        var pending = await GetPendingSourceAsync(
            pipeline.Id,
            sourceReferenceId,
            cancellationToken);
        if (pending is null)
        {
            return PendingSourceUnavailable(pipeline);
        }

        var comparison = _schemaComparisonService.Compare(
            pipeline.ExpectedSchema,
            pending.DetectedSchema,
            pipeline.FieldMappings ?? []);
        var model = CreateMappingModelFromSchema(
            pending.DetectedSchema,
            pipeline.FieldMappings ?? [],
            includeNewFieldsByDefault: false);
        model.PendingSourceReferenceId = sourceReferenceId;
        model.SchemaDifference = CreateSchemaDifferenceModel(comparison);
        return View("Mapping", model);
    }

    private async Task<IActionResult> SaveRemappingAsync(
        Guid id,
        PipelineDefinition pipeline,
        Guid sourceReferenceId,
        FieldMappingViewModel model,
        CancellationToken cancellationToken)
    {
        var pending = await GetPendingSourceAsync(id, sourceReferenceId, cancellationToken);
        if (pending is null)
        {
            return PendingSourceUnavailable(pipeline);
        }

        var comparison = _schemaComparisonService.Compare(
            pipeline.ExpectedSchema,
            pending.DetectedSchema,
            pipeline.FieldMappings ?? []);
        model.SchemaDifference = CreateSchemaDifferenceModel(comparison);

        if (!MatchesExpectedSchema(model.Fields, pending.DetectedSchema))
        {
            ModelState.Clear();
            ModelState.AddModelError(
                string.Empty,
                "The submitted mapping fields do not match the inspected source schema. Reload the page and try again.");
            return await CreateRemappingViewAsync(pipeline, sourceReferenceId, cancellationToken);
        }

        ApplyTrustedFieldTypes(model, pending.DetectedSchema);
        if (!ModelState.IsValid)
        {
            return View("Mapping", model);
        }

        var mappings = model.Fields.Select(field => new FieldMapping
        {
            SourceField = field.SourceField,
            TargetField = field.TargetField ?? string.Empty,
            IsIncluded = field.IsIncluded
        }).ToList();
        var configuration = new PipelineDefinition
        {
            ExpectedSchema = CopySchema(pending.DetectedSchema),
            FieldMappings = mappings
        };

        try
        {
            _fieldMappingService.Prepare(configuration);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return View("Mapping", model);
        }

        var commitStatus = await CommitPendingSourceAsync(
            id,
            pipeline,
            sourceReferenceId,
            pending,
            mappings,
            cancellationToken);
        if (commitStatus == PipelineSourceCommitStatus.PersistenceRejected)
        {
            return NotFound();
        }

        AddActivationFailure(commitStatus);
        if (commitStatus == PipelineSourceCommitStatus.Succeeded)
        {
            model.IsSaved = true;
        }

        return View("Mapping", model);
    }

    private async Task<PendingSourceInspection?> GetPendingSourceAsync(
        Guid pipelineId,
        Guid sourceReferenceId,
        CancellationToken cancellationToken)
    {
        if (_sourceInspectionService is null)
        {
            throw new InvalidOperationException("Source inspection is not configured.");
        }

        return await _sourceInspectionService.GetPendingSourceAsync(
            pipelineId,
            sourceReferenceId,
            cancellationToken);
    }

    private IActionResult PendingSourceUnavailable(PipelineDefinition pipeline)
    {
        ModelState.AddModelError(
            string.Empty,
            "The uploaded source is no longer available. Upload and inspect it again.");
        return View("Source", CreateSourceModel(pipeline));
    }

    private SourceUploadViewModel CreateSourceModel(PipelineDefinition pipeline)
    {
        var postgreSqlSource = pipeline.PostgreSqlSource;
        var model = new SourceUploadViewModel
        {
            SourceType = pipeline.SourceType is SourceType.Xlsx or SourceType.PostgreSql or SourceType.MongoDb
                ? pipeline.SourceType
                : SourceType.Csv,
            Delimiter = pipeline.SourceOptions.Delimiter ?? CsvDelimiter.Comma,
            WorksheetName = pipeline.SourceOptions.WorksheetName,
            PostgreSqlConnectionProfile = postgreSqlSource?.ConnectionProfile,
            PostgreSqlDatabase = postgreSqlSource?.Database,
            PostgreSqlSchema = postgreSqlSource?.Schema,
            PostgreSqlTable = postgreSqlSource?.Table,
            LoadedPostgreSqlConnectionProfile = postgreSqlSource?.ConnectionProfile,
            LoadedPostgreSqlDatabase = postgreSqlSource?.Database,
            LoadedPostgreSqlSchema = postgreSqlSource?.Schema,
            MongoDbDatabase = pipeline.MongoDbSource?.Database,
            MongoDbCollection = pipeline.MongoDbSource?.Collection,
            LoadedMongoDbDatabase = pipeline.MongoDbSource?.Database
        };
        PopulatePostgreSqlConnectionProfiles(model);
        return model;
    }

    private async Task<IActionResult> ConfigurePostgreSqlSourceAsync(
        Guid id,
        PipelineDefinition pipeline,
        SourceUploadViewModel model,
        string? postgreSqlAction,
        CancellationToken cancellationToken)
    {
        NormalizePostgreSqlSelection(model);
        PopulatePostgreSqlConnectionProfiles(model);

        if (_postgreSqlMetadataDiscoveryService is null
            || _postgreSqlConnectionProfileCatalog is null)
        {
            ModelState.AddModelError(string.Empty, "PostgreSQL source configuration is not available.");
            return PostgreSqlSourceView(model);
        }

        if (string.IsNullOrWhiteSpace(model.PostgreSqlConnectionProfile))
        {
            if (string.Equals(postgreSqlAction, "configure", StringComparison.Ordinal))
            {
                ModelState.AddModelError(
                    nameof(model.PostgreSqlConnectionProfile),
                    "Choose a PostgreSQL connection profile.");
            }

            return PostgreSqlSourceView(model);
        }

        if (!model.PostgreSqlConnectionProfiles.Contains(
                model.PostgreSqlConnectionProfile,
                StringComparer.Ordinal))
        {
            ModelState.AddModelError(
                nameof(model.PostgreSqlConnectionProfile),
                "The selected PostgreSQL connection profile is no longer available.");
            return PostgreSqlSourceView(model);
        }

        try
        {
            model.PostgreSqlDatabases = (await _postgreSqlMetadataDiscoveryService
                    .DiscoverDatabasesAsync(model.PostgreSqlConnectionProfile, cancellationToken))
                .Select(database => database.Name)
                .ToList();

            if (!RequirePostgreSqlSelection(
                    model,
                    nameof(model.PostgreSqlDatabase),
                    model.PostgreSqlDatabase,
                    model.PostgreSqlDatabases,
                    "database",
                    postgreSqlAction))
            {
                return PostgreSqlSourceView(model);
            }

            model.PostgreSqlSchemas = (await _postgreSqlMetadataDiscoveryService
                    .DiscoverSchemasAsync(
                        model.PostgreSqlConnectionProfile,
                        model.PostgreSqlDatabase!,
                        cancellationToken))
                .Select(schema => schema.Name)
                .ToList();

            if (!RequirePostgreSqlSelection(
                    model,
                    nameof(model.PostgreSqlSchema),
                    model.PostgreSqlSchema,
                    model.PostgreSqlSchemas,
                    "schema",
                    postgreSqlAction))
            {
                return PostgreSqlSourceView(model);
            }

            model.PostgreSqlTables = (await _postgreSqlMetadataDiscoveryService
                    .DiscoverTablesAsync(
                        model.PostgreSqlConnectionProfile,
                        model.PostgreSqlDatabase!,
                        model.PostgreSqlSchema!,
                        cancellationToken))
                .Select(table => table.Name)
                .ToList();

            if (!RequirePostgreSqlSelection(
                    model,
                    nameof(model.PostgreSqlTable),
                    model.PostgreSqlTable,
                    model.PostgreSqlTables,
                    "table",
                    postgreSqlAction))
            {
                return PostgreSqlSourceView(model);
            }

            if (!string.Equals(postgreSqlAction, "configure", StringComparison.Ordinal))
            {
                return PostgreSqlSourceView(model);
            }

            var schema = _postgreSqlSourceSchemaConverter.Convert(
                await _postgreSqlMetadataDiscoveryService.DiscoverColumnsAsync(
                    model.PostgreSqlConnectionProfile,
                    model.PostgreSqlDatabase!,
                    model.PostgreSqlSchema!,
                    model.PostgreSqlTable!,
                    cancellationToken));
            if (schema.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "The selected PostgreSQL table has no columns to map.");
                return PostgreSqlSourceView(model);
            }

            var comparison = pipeline.ExpectedSchema.Count > 0
                ? _schemaComparisonService.Compare(
                    pipeline.ExpectedSchema,
                    schema,
                    pipeline.FieldMappings ?? [])
                : null;
            var replacement = CopyPipeline(pipeline);
            replacement.SourceType = SourceType.PostgreSql;
            replacement.MongoDbSource = null;
            replacement.PostgreSqlSource = new PostgreSqlSourceOptions
            {
                ConnectionProfile = model.PostgreSqlConnectionProfile,
                Database = model.PostgreSqlDatabase!,
                Schema = model.PostgreSqlSchema!,
                Table = model.PostgreSqlTable!
            };
            replacement.ExpectedSchema = CopySchema(schema);
            replacement.FieldMappings = ReconcileMappings(pipeline, schema);
            replacement.RequiresRemapping = RequiresExplicitSchemaConfirmation(comparison);

            if (!await _pipelineService.UpdateAsync(id, replacement, cancellationToken))
            {
                return NotFound();
            }

            await RetireFileSourceAfterDatabaseSwitchAsync(pipeline, id);

            if (RequiresExplicitSchemaConfirmation(comparison))
            {
                var mappingModel = CreateMappingModelFromSchema(
                    replacement.ExpectedSchema,
                    replacement.FieldMappings,
                    includeNewFieldsByDefault: false);
                mappingModel.SchemaDifference = CreateSchemaDifferenceModel(comparison!);
                return View("Mapping", mappingModel);
            }

            return RedirectToAction(nameof(Mapping), new { id });
        }
        catch (Exception exception) when (exception is PostgreSqlConnectionProfileNotFoundException
                                         or PostgreSqlConnectionAccessException
                                         or PostgreSqlMetadataObjectNotFoundException
                                         or PostgreSqlUnsupportedColumnTypeException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return PostgreSqlSourceView(model);
        }
    }

    private bool RequirePostgreSqlSelection(
        SourceUploadViewModel model,
        string propertyName,
        string? selectedValue,
        IReadOnlyList<string> availableValues,
        string displayName,
        string? postgreSqlAction)
    {
        if (string.IsNullOrWhiteSpace(selectedValue))
        {
            if (string.Equals(postgreSqlAction, "configure", StringComparison.Ordinal))
            {
                ModelState.AddModelError(propertyName, $"Choose a PostgreSQL {displayName}.");
            }

            return false;
        }

        if (availableValues.Contains(selectedValue, StringComparer.Ordinal))
        {
            return true;
        }

        ClearPostgreSqlSelection(model, propertyName);
        ModelState.AddModelError(
            propertyName,
            $"The selected PostgreSQL {displayName} is no longer available.");
        return false;
    }

    private void NormalizePostgreSqlSelection(SourceUploadViewModel model)
    {
        if (!string.Equals(
                model.PostgreSqlConnectionProfile,
                model.LoadedPostgreSqlConnectionProfile,
                StringComparison.Ordinal))
        {
            ClearPostgreSqlSelection(model, nameof(model.PostgreSqlDatabase));
            return;
        }

        if (!string.Equals(model.PostgreSqlDatabase, model.LoadedPostgreSqlDatabase, StringComparison.Ordinal))
        {
            ClearPostgreSqlSelection(model, nameof(model.PostgreSqlSchema));
            return;
        }

        if (!string.Equals(model.PostgreSqlSchema, model.LoadedPostgreSqlSchema, StringComparison.Ordinal))
        {
            ClearPostgreSqlSelection(model, nameof(model.PostgreSqlTable));
        }
    }

    private void ClearPostgreSqlSelection(SourceUploadViewModel model, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(SourceUploadViewModel.PostgreSqlDatabase):
                model.PostgreSqlDatabase = null;
                model.PostgreSqlSchema = null;
                model.PostgreSqlTable = null;
                ModelState.Remove(nameof(model.PostgreSqlDatabase));
                ModelState.Remove(nameof(model.PostgreSqlSchema));
                ModelState.Remove(nameof(model.PostgreSqlTable));
                break;
            case nameof(SourceUploadViewModel.PostgreSqlSchema):
                model.PostgreSqlSchema = null;
                model.PostgreSqlTable = null;
                ModelState.Remove(nameof(model.PostgreSqlSchema));
                ModelState.Remove(nameof(model.PostgreSqlTable));
                break;
            case nameof(SourceUploadViewModel.PostgreSqlTable):
                model.PostgreSqlTable = null;
                ModelState.Remove(nameof(model.PostgreSqlTable));
                break;
        }
    }

    private void PopulatePostgreSqlConnectionProfiles(SourceUploadViewModel model) =>
        model.PostgreSqlConnectionProfiles = _postgreSqlConnectionProfileCatalog?
            .GetProfileNames()
            .OrderBy(profileName => profileName, StringComparer.Ordinal)
            .ToList()
            ?? [];

    private IActionResult PostgreSqlSourceView(SourceUploadViewModel model)
    {
        ModelState.Remove(nameof(model.LoadedPostgreSqlConnectionProfile));
        ModelState.Remove(nameof(model.LoadedPostgreSqlDatabase));
        ModelState.Remove(nameof(model.LoadedPostgreSqlSchema));
        model.LoadedPostgreSqlConnectionProfile = model.PostgreSqlConnectionProfile;
        model.LoadedPostgreSqlDatabase = model.PostgreSqlDatabase;
        model.LoadedPostgreSqlSchema = model.PostgreSqlSchema;
        return View("Source", model);
    }

    private async Task<IActionResult> ConfigureMongoDbSourceAsync(
        Guid id,
        PipelineDefinition pipeline,
        SourceUploadViewModel model,
        string? mongoDbAction,
        CancellationToken cancellationToken)
    {
        NormalizeMongoDbSelection(model);
        if (_mongoSourceMetadataDiscoveryService is null
            || _mongoSourceSchemaInferenceService is null)
        {
            ModelState.AddModelError(string.Empty, "MongoDB source configuration is not available.");
            return MongoDbSourceView(model);
        }

        try
        {
            model.MongoDbDatabases = (await _mongoSourceMetadataDiscoveryService
                    .DiscoverDatabasesAsync(cancellationToken))
                .Select(database => database.Name)
                .ToList();
            if (!RequireMongoDbSelection(
                    model,
                    nameof(model.MongoDbDatabase),
                    model.MongoDbDatabase,
                    model.MongoDbDatabases,
                    "database",
                    mongoDbAction))
            {
                return MongoDbSourceView(model);
            }

            model.MongoDbCollections = (await _mongoSourceMetadataDiscoveryService
                    .DiscoverCollectionsAsync(model.MongoDbDatabase!, cancellationToken))
                .Select(collection => collection.Name)
                .ToList();
            if (!RequireMongoDbSelection(
                    model,
                    nameof(model.MongoDbCollection),
                    model.MongoDbCollection,
                    model.MongoDbCollections,
                    "collection",
                    mongoDbAction))
            {
                return MongoDbSourceView(model);
            }

            if (!string.Equals(mongoDbAction, "configure", StringComparison.Ordinal))
            {
                return MongoDbSourceView(model);
            }

            var source = new MongoDbSourceOptions
            {
                Database = model.MongoDbDatabase!,
                Collection = model.MongoDbCollection!
            };
            var schema = await _mongoSourceSchemaInferenceService
                .InferAsync(source, cancellationToken);
            if (schema.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "The selected MongoDB collection has no fields to map.");
                return MongoDbSourceView(model);
            }

            var comparison = pipeline.ExpectedSchema.Count > 0
                ? _schemaComparisonService.Compare(
                    pipeline.ExpectedSchema,
                    schema,
                    pipeline.FieldMappings ?? [])
                : null;
            var replacement = CopyPipeline(pipeline);
            replacement.SourceType = SourceType.MongoDb;
            replacement.PostgreSqlSource = null;
            replacement.MongoDbSource = source;
            replacement.ExpectedSchema = CopySchema(schema);
            replacement.FieldMappings = ReconcileMappings(pipeline, schema);
            replacement.RequiresRemapping = RequiresExplicitSchemaConfirmation(comparison);

            if (!await _pipelineService.UpdateAsync(id, replacement, cancellationToken))
            {
                return NotFound();
            }

            await RetireFileSourceAfterDatabaseSwitchAsync(pipeline, id);

            if (RequiresExplicitSchemaConfirmation(comparison))
            {
                var mappingModel = CreateMappingModelFromSchema(
                    replacement.ExpectedSchema,
                    replacement.FieldMappings,
                    includeNewFieldsByDefault: false);
                mappingModel.SchemaDifference = CreateSchemaDifferenceModel(comparison!);
                return View("Mapping", mappingModel);
            }

            return RedirectToAction(nameof(Mapping), new { id });
        }
        catch (Exception exception) when (exception is MongoSourceAccessException
                                         or MongoSourceMetadataObjectNotFoundException
                                         or MongoSourceSchemaInferenceException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return MongoDbSourceView(model);
        }
    }

    private bool RequireMongoDbSelection(
        SourceUploadViewModel model,
        string propertyName,
        string? selectedValue,
        IReadOnlyList<string> availableValues,
        string displayName,
        string? mongoDbAction)
    {
        if (string.IsNullOrWhiteSpace(selectedValue))
        {
            if (string.Equals(mongoDbAction, "configure", StringComparison.Ordinal))
            {
                ModelState.AddModelError(propertyName, $"Choose a MongoDB {displayName}.");
            }

            return false;
        }

        if (availableValues.Contains(selectedValue, StringComparer.Ordinal))
        {
            return true;
        }

        ClearMongoDbSelection(model, propertyName);
        ModelState.AddModelError(
            propertyName,
            $"The selected MongoDB {displayName} is no longer available.");
        return false;
    }

    private void NormalizeMongoDbSelection(SourceUploadViewModel model)
    {
        if (!string.Equals(model.MongoDbDatabase, model.LoadedMongoDbDatabase, StringComparison.Ordinal))
        {
            model.MongoDbCollection = null;
            ModelState.Remove(nameof(model.MongoDbCollection));
        }
    }

    private void ClearMongoDbSelection(SourceUploadViewModel model, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(SourceUploadViewModel.MongoDbDatabase):
                model.MongoDbDatabase = null;
                model.MongoDbCollection = null;
                ModelState.Remove(nameof(model.MongoDbDatabase));
                ModelState.Remove(nameof(model.MongoDbCollection));
                break;
            case nameof(SourceUploadViewModel.MongoDbCollection):
                model.MongoDbCollection = null;
                ModelState.Remove(nameof(model.MongoDbCollection));
                break;
        }
    }

    private IActionResult MongoDbSourceView(SourceUploadViewModel model)
    {
        ModelState.Remove(nameof(model.LoadedMongoDbDatabase));
        model.LoadedMongoDbDatabase = model.MongoDbDatabase;
        return View("Source", model);
    }

    private async Task RetireFileSourceAfterDatabaseSwitchAsync(
        PipelineDefinition priorPipeline,
        Guid pipelineId)
    {
        if (priorPipeline.SourceType is not SourceType.Csv and not SourceType.Xlsx
            || _wizardSourceStore is null)
        {
            return;
        }

        try
        {
            await _wizardSourceStore.RetireActiveAsync(pipelineId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                exception,
                "The prior file source for pipeline {PipelineId} could not be removed immediately.",
                pipelineId);
        }
    }

    private FieldMappingViewModel CreateMappingModel(
        PipelineDefinition pipeline,
        out string? mappingError)
    {
        mappingError = null;
        var schema = pipeline.ExpectedSchema;
        var mappings = pipeline.FieldMappings;

        if (mappings is null)
        {
            mappingError = "The saved mapping configuration is invalid. Review and save the mapping again.";
            return CreateDefaultMappingModel(schema);
        }

        if (mappings.Count > 0)
        {
            try
            {
                _fieldMappingService.Prepare(new PipelineDefinition
                {
                    ExpectedSchema = CopySchema(schema),
                    FieldMappings = CopyMappings(mappings)
                });

                return CreateMappingModelFromSchema(schema, mappings, includeNewFieldsByDefault: false);
            }
            catch (InvalidOperationException)
            {
                // Fall through to authoritative defaults below.
            }

            mappingError = "The saved mapping configuration is invalid. Review and save the mapping again.";
        }

        return CreateDefaultMappingModel(schema);
    }

    private static FieldMappingViewModel CreateDefaultMappingModel(
        IReadOnlyList<SourceFieldDefinition> schema) => new()
    {
        Fields = schema.Select(field => new FieldMappingFieldViewModel
        {
            SourceField = field.Name,
            TargetField = field.Name,
            DataType = field.DataType,
            IsIncluded = true
        }).ToList()
    };

    private static FieldMappingViewModel CreateMappingModelFromSchema(
        IReadOnlyList<SourceFieldDefinition> schema,
        IReadOnlyList<FieldMapping> mappings,
        bool includeNewFieldsByDefault)
    {
        var mappingsBySource = new Dictionary<string, FieldMapping>(StringComparer.Ordinal);
        foreach (var mapping in mappings.Where(mapping => mapping is not null))
        {
            mappingsBySource.TryAdd(mapping.SourceField, mapping);
        }

        return new FieldMappingViewModel
        {
            Fields = schema.Select(field => mappingsBySource.TryGetValue(field.Name, out var mapping)
                ? new FieldMappingFieldViewModel
                {
                    SourceField = field.Name,
                    TargetField = mapping.TargetField,
                    DataType = field.DataType,
                    IsIncluded = mapping.IsIncluded
                }
                : new FieldMappingFieldViewModel
                {
                    SourceField = field.Name,
                    TargetField = includeNewFieldsByDefault ? field.Name : string.Empty,
                    DataType = field.DataType,
                    IsIncluded = includeNewFieldsByDefault
                }).ToList()
        };
    }

    private static bool MatchesExpectedSchema(
        IReadOnlyList<FieldMapping> mappings,
        IReadOnlyList<SourceFieldDefinition> schema)
    {
        if (mappings.Count != schema.Count) return false;

        for (var index = 0; index < schema.Count; index++)
        {
            if (mappings[index] is null
                || !string.Equals(mappings[index].SourceField, schema[index].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesExpectedSchema(
        IReadOnlyList<FieldMappingFieldViewModel> fields,
        IReadOnlyList<SourceFieldDefinition> schema)
    {
        if (fields.Count != schema.Count) return false;

        for (var index = 0; index < schema.Count; index++)
        {
            if (!string.Equals(fields[index].SourceField, schema[index].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyTrustedFieldTypes(
        FieldMappingViewModel model,
        IReadOnlyList<SourceFieldDefinition> schema)
    {
        var count = Math.Min(model.Fields.Count, schema.Count);
        for (var index = 0; index < count; index++)
        {
            model.Fields[index].DataType = schema[index].DataType;
        }
    }

    private static List<SourceFieldDefinition> CopySchema(
        IReadOnlyList<SourceFieldDefinition> schema) => schema
        .Select(field => new SourceFieldDefinition { Name = field.Name, DataType = field.DataType })
        .ToList();

    private static List<FieldMapping> CopyMappings(
        IEnumerable<FieldMapping> mappings) => mappings
        .Select(mapping => new FieldMapping
        {
            SourceField = mapping.SourceField,
            TargetField = mapping.TargetField,
            IsIncluded = mapping.IsIncluded
        })
        .ToList();

    private static PipelineDefinition CopyPipeline(PipelineDefinition pipeline) => new()
    {
        Id = pipeline.Id,
        Name = pipeline.Name,
        Description = pipeline.Description,
        SourceType = pipeline.SourceType,
        SourceOptions = CopySourceOptions(pipeline.SourceOptions),
        PostgreSqlSource = CopyPostgreSqlSourceOptions(pipeline.PostgreSqlSource),
        MongoDbSource = CopyMongoDbSourceOptions(pipeline.MongoDbSource),
        ExpectedSchema = CopySchema(pipeline.ExpectedSchema),
        FieldMappings = CopyMappings(pipeline.FieldMappings ?? []),
        RequiresRemapping = pipeline.RequiresRemapping,
        TransformationRules = (pipeline.TransformationRules ?? [])
            .Select(rule => new TransformationRule
            {
                Id = rule.Id,
                Type = rule.Type,
                Order = rule.Order,
                SourceField = rule.SourceField,
                Configuration = new Dictionary<string, string>(
                    rule.Configuration,
                    StringComparer.Ordinal)
            })
            .ToList(),
        ValidationRules = (pipeline.ValidationRules ?? [])
            .Select(rule => new ValidationRule
            {
                Id = rule.Id,
                Type = rule.Type,
                Field = rule.Field,
                Configuration = new Dictionary<string, string>(
                    rule.Configuration,
                    StringComparer.Ordinal),
                ErrorMessage = rule.ErrorMessage
            })
            .ToList(),
        DestinationDatabase = pipeline.DestinationDatabase,
        DestinationCollection = pipeline.DestinationCollection,
        UpsertKeyField = pipeline.UpsertKeyField,
        CreatedAt = pipeline.CreatedAt,
        UpdatedAt = pipeline.UpdatedAt
    };

    private static PipelineDefinition CopyPipelineWithMappings(
        PipelineDefinition pipeline,
        List<FieldMapping> fieldMappings) => new()
    {
        Id = pipeline.Id,
        Name = pipeline.Name,
        Description = pipeline.Description,
        SourceType = pipeline.SourceType,
        SourceOptions = pipeline.SourceOptions,
        PostgreSqlSource = CopyPostgreSqlSourceOptions(pipeline.PostgreSqlSource),
        MongoDbSource = CopyMongoDbSourceOptions(pipeline.MongoDbSource),
        ExpectedSchema = pipeline.ExpectedSchema,
        FieldMappings = fieldMappings,
        RequiresRemapping = pipeline.RequiresRemapping,
        TransformationRules = pipeline.TransformationRules,
        ValidationRules = pipeline.ValidationRules,
        DestinationDatabase = pipeline.DestinationDatabase,
        DestinationCollection = pipeline.DestinationCollection,
        UpsertKeyField = pipeline.UpsertKeyField,
        CreatedAt = pipeline.CreatedAt,
        UpdatedAt = pipeline.UpdatedAt
    };

    private static SourceOptions CreateCsvOptions(PipelineDefinition pipeline, CsvDelimiter delimiter) => new()
    {
        Delimiter = delimiter, FirstRowIsHeader = true, CultureName = pipeline.SourceOptions.CultureName,
        DateFormat = pipeline.SourceOptions.DateFormat
    };

    private static SourceOptions CopySourceOptions(SourceOptions options) => new()
    {
        Delimiter = options.Delimiter,
        WorksheetName = options.WorksheetName,
        FirstRowIsHeader = options.FirstRowIsHeader,
        CultureName = options.CultureName,
        DateFormat = options.DateFormat
    };

    private static PostgreSqlSourceOptions? CopyPostgreSqlSourceOptions(
        PostgreSqlSourceOptions? options) => options is null
        ? null
        : new PostgreSqlSourceOptions
        {
            ConnectionProfile = options.ConnectionProfile,
            Database = options.Database,
            Schema = options.Schema,
            Table = options.Table
        };

    private static MongoDbSourceOptions? CopyMongoDbSourceOptions(
        MongoDbSourceOptions? options) => options is null
        ? null
        : new MongoDbSourceOptions
        {
            Database = options.Database,
            Collection = options.Collection
        };

    private static List<FieldMapping> ReconcileMappings(
        PipelineDefinition pipeline,
        IReadOnlyList<SourceFieldDefinition> inspectedSchema)
    {
        var persistedMappings = pipeline.FieldMappings ?? [];
        if (pipeline.ExpectedSchema.Count == 0)
        {
            return CopyMappings(persistedMappings);
        }

        var inspectedNames = new HashSet<string>(
            inspectedSchema.Select(field => field.Name),
            StringComparer.Ordinal);
        var reconciled = persistedMappings
            .Where(mapping => mapping is not null && inspectedNames.Contains(mapping.SourceField))
            .Select(mapping => new FieldMapping
            {
                SourceField = mapping.SourceField,
                TargetField = mapping.TargetField,
                IsIncluded = mapping.IsIncluded
            })
            .ToList();
        var mappedNames = new HashSet<string>(
            reconciled.Select(mapping => mapping.SourceField),
            StringComparer.Ordinal);

        foreach (var field in inspectedSchema)
        {
            if (mappedNames.Add(field.Name))
            {
                reconciled.Add(new FieldMapping
                {
                    SourceField = field.Name,
                    TargetField = string.Empty,
                    IsIncluded = false
                });
            }
        }

        return reconciled;
    }

    private static bool IsSupportedDelimiter(CsvDelimiter delimiter) =>
        delimiter is CsvDelimiter.Comma or CsvDelimiter.Semicolon or CsvDelimiter.Tab;

    private async Task<bool> SaveSourceAsync(
        Guid id,
        PipelineDefinition pipeline,
        SourceType type,
        SourceOptions options,
        IReadOnlyList<SourceFieldDefinition> schema,
        IReadOnlyList<FieldMapping> mappings,
        CancellationToken ct)
    {
        var replacement = new PipelineDefinition
        {
            Id = pipeline.Id,
            Name = pipeline.Name,
            Description = pipeline.Description,
            SourceType = type,
            SourceOptions = CopySourceOptions(options),
            PostgreSqlSource = null,
            MongoDbSource = null,
            ExpectedSchema = CopySchema(schema),
            FieldMappings = CopyMappings(mappings),
            RequiresRemapping = false,
            TransformationRules = pipeline.TransformationRules,
            ValidationRules = pipeline.ValidationRules,
            DestinationDatabase = pipeline.DestinationDatabase,
            DestinationCollection = pipeline.DestinationCollection,
            UpsertKeyField = pipeline.UpsertKeyField,
            CreatedAt = pipeline.CreatedAt,
            UpdatedAt = pipeline.UpdatedAt
        };
        return await _pipelineService.UpdateAsync(id, replacement, ct);
    }

    private async Task<PipelineSourceCommitStatus> CommitSourceAsync(
        Guid id,
        PipelineDefinition pipeline,
        SourceType type,
        SourceOptions options,
        SourceInspectionResult inspection,
        IReadOnlyList<FieldMapping> mappings,
        CancellationToken cancellationToken)
    {
        if (_sourceCommitCoordinator is null)
        {
            if (_wizardSourceStore is not null)
            {
                throw new InvalidOperationException("Pipeline source commit coordination is not configured.");
            }

            return await SaveSourceAsync(
                    id,
                    pipeline,
                    type,
                    options,
                    inspection.DetectedSchema,
                    mappings,
                    cancellationToken)
                ? PipelineSourceCommitStatus.Succeeded
                : PipelineSourceCommitStatus.PersistenceRejected;
        }

        if (inspection.SourceReferenceId is not Guid sourceReferenceId)
        {
            return PipelineSourceCommitStatus.ActivationFailed;
        }

        return await _sourceCommitCoordinator.CommitAsync(
            id,
            sourceReferenceId,
            token => SaveSourceAsync(
                id,
                pipeline,
                type,
                options,
                inspection.DetectedSchema,
                mappings,
                token),
            cancellationToken);
    }

    private async Task<PipelineSourceCommitStatus> CommitPendingSourceAsync(
        Guid id,
        PipelineDefinition pipeline,
        Guid sourceReferenceId,
        PendingSourceInspection pending,
        IReadOnlyList<FieldMapping> mappings,
        CancellationToken cancellationToken)
    {
        if (_sourceCommitCoordinator is null)
        {
            if (_wizardSourceStore is not null)
            {
                throw new InvalidOperationException("Pipeline source commit coordination is not configured.");
            }

            return await SaveSourceAsync(
                    id,
                    pipeline,
                    pending.SourceType,
                    pending.SourceOptions,
                    pending.DetectedSchema,
                    mappings,
                    cancellationToken)
                ? PipelineSourceCommitStatus.Succeeded
                : PipelineSourceCommitStatus.PersistenceRejected;
        }

        var priorPipeline = CopyPipeline(pipeline);
        return await _sourceCommitCoordinator.CommitRemapAsync(
            id,
            sourceReferenceId,
            token => SaveSourceAsync(
                id,
                pipeline,
                pending.SourceType,
                pending.SourceOptions,
                pending.DetectedSchema,
                mappings,
                token),
            token => _pipelineService.UpdateAsync(
                id,
                CopyPipeline(priorPipeline),
                token),
            cancellationToken);
    }

    private void AddActivationFailure(PipelineSourceCommitStatus commitStatus)
    {
        if (commitStatus == PipelineSourceCommitStatus.ActivationFailed)
        {
            ModelState.AddModelError(
                string.Empty,
                "The inspected source could not be retained for preview. Upload the source again.");
        }
    }

    private static SchemaDifferenceViewModel CreateSchemaDifferenceModel(
        SourceSchemaComparisonResult comparison) => new()
    {
        MissingFields = comparison.MissingFields.Select(field => new SchemaFieldViewModel
        {
            Name = field.Name,
            DataType = field.DataType
        }).ToList(),
        NewFields = comparison.NewFields.Select(field => new SchemaFieldViewModel
        {
            Name = field.Name,
            DataType = field.DataType
        }).ToList(),
        TypeChanges = comparison.TypeChanges.Select(change => new SchemaTypeChangeViewModel
        {
            FieldName = change.FieldName,
            SavedType = change.SavedType,
            InspectedType = change.InspectedType
        }).ToList(),
        UnresolvedMappings = comparison.UnresolvedMappings.Select(mapping => new UnresolvedMappingViewModel
        {
            SourceField = mapping.SourceField,
            TargetField = mapping.TargetField,
            IsIncluded = mapping.IsIncluded
        }).ToList()
    };

    private static bool RequiresExplicitSchemaConfirmation(
        SourceSchemaComparisonResult? comparison) =>
        comparison is not null
        && (comparison.HasDifferences || comparison.HasUnresolvedMappings);

    private IActionResult ApplyInspection(
        SourceUploadViewModel model,
        SourceInspectionResult inspection,
        SourceSchemaComparisonResult? comparison = null)
    {
        if (!inspection.IsSuccess) ModelState.AddModelError(string.Empty, inspection.ErrorMessage!);
        model.StageId = inspection.StageId;
        model.WorksheetNames = inspection.WorksheetNames;
        model.Columns = inspection.Columns;
        model.SampleRows = inspection.SampleRows.Select(row => new SourceSampleRowViewModel
        {
            SourceRowNumber = row.SourceRowNumber,
            Values = inspection.Columns.Select(column => Convert.ToString(row.Values.GetValueOrDefault(column), System.Globalization.CultureInfo.InvariantCulture)).ToList()
        }).ToList();
        model.SchemaDifference = comparison is not null
            && (comparison.HasDifferences || comparison.HasUnresolvedMappings)
                ? CreateSchemaDifferenceModel(comparison)
                : null;
        return View("Source", model);
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var pipelines = await _pipelineService.ListAsync(cancellationToken);
        IReadOnlyList<PipelineListItemViewModel> model = pipelines
            .OrderByDescending(pipeline => pipeline.UpdatedAt)
            .Select(pipeline => new PipelineListItemViewModel
            {
                Id = pipeline.Id,
                Name = pipeline.Name,
                SourceType = pipeline.SourceType,
                DestinationDatabase = pipeline.DestinationDatabase,
                DestinationCollection = pipeline.DestinationCollection,
                UpdatedAt = pipeline.UpdatedAt
            })
            .ToList();

        return View(model);
    }

    public IActionResult Create()
    {
        return View(new PipelineFormViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        PipelineFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var pipeline = new PipelineDefinition
        {
            Name = model.Name,
            Description = model.Description
        };

        try
        {
            pipeline = await _pipelineService.CreateAsync(pipeline, cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "pipeline")
        {
            ModelState.AddModelError(nameof(PipelineFormViewModel.Name), exception.Message);
            return View(model);
        }
        catch (DuplicatePipelineDefinitionException)
        {
            ModelState.AddModelError(
                string.Empty,
                "The pipeline could not be created because of an identity conflict. Please try again.");
            return View(model);
        }

        return RedirectToAction(nameof(Source), new { id = pipeline.Id });
    }

    public async Task<IActionResult> Edit(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        ViewData["PipelineId"] = id;
        var model = new PipelineFormViewModel
        {
            Name = pipeline.Name,
            Description = pipeline.Description,
            DestinationDatabase = pipeline.DestinationDatabase,
            DestinationCollection = pipeline.DestinationCollection,
            UpsertKeyField = pipeline.UpsertKeyField
        };
        PopulateAvailableMappedFields(model, pipeline);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        [FromRoute] Guid id,
        PipelineFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        ViewData["PipelineId"] = id;

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        PopulateAvailableMappedFields(model, pipeline);

        var hasDestinationConfiguration = !string.IsNullOrWhiteSpace(model.DestinationDatabase)
            || !string.IsNullOrWhiteSpace(model.DestinationCollection);
        if (hasDestinationConfiguration)
        {
            ValidateDestination(model);

            if (string.IsNullOrWhiteSpace(model.UpsertKeyField)
                || !model.AvailableMappedFields.Contains(model.UpsertKeyField, StringComparer.Ordinal))
            {
                ModelState.AddModelError(
                    nameof(PipelineFormViewModel.UpsertKeyField),
                    "Choose an included mapped output field as the upsert key.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(model.UpsertKeyField)
                 && !model.AvailableMappedFields.Contains(model.UpsertKeyField, StringComparer.Ordinal))
        {
            ModelState.AddModelError(
                nameof(PipelineFormViewModel.UpsertKeyField),
                "Choose an included mapped output field as the upsert key.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (hasDestinationConfiguration && _targetAccessService is not null)
        {
            try
            {
                await _targetAccessService.EnsureAccessibleAsync(
                    new MongoTarget(model.DestinationDatabase!, model.DestinationCollection!),
                    cancellationToken);
            }
            catch (MongoTargetAccessException)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "The MongoDB destination could not be accessed. Check the destination and try again.");
                return View(model);
            }
        }

        pipeline.Name = model.Name;
        pipeline.Description = model.Description;
        pipeline.DestinationDatabase = model.DestinationDatabase ?? string.Empty;
        pipeline.DestinationCollection = model.DestinationCollection ?? string.Empty;
        pipeline.UpsertKeyField = model.UpsertKeyField ?? string.Empty;

        try
        {
            if (!await _pipelineService.UpdateAsync(id, pipeline, cancellationToken))
            {
                return NotFound();
            }
        }
        catch (ArgumentException exception) when (exception.ParamName == "pipeline")
        {
            ModelState.AddModelError(nameof(PipelineFormViewModel.Name), exception.Message);
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Preview(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        if (_sourceCommitCoordinator is null || _readinessService is null || _previewService is null)
        {
            throw new InvalidOperationException("Pipeline preview is not configured.");
        }

        _logger?.LogInformation(
            "Preview request started for pipeline {PipelineId}; request {RequestId}; process {ProcessId}.",
            id,
            HttpContext.TraceIdentifier,
            Environment.ProcessId);
        PipelineDefinition? pipeline = null;
        try
        {
            await using var snapshot = await _sourceCommitCoordinator.CapturePreviewAsync(
                id,
                token => _pipelineService.GetByIdAsync(id, token),
                _readinessService.EvaluateForPreview,
                cancellationToken);
            if (snapshot.Status == PipelinePreviewSnapshotStatus.NotFound)
            {
                return NotFound();
            }

            pipeline = snapshot.Pipeline
                ?? throw new InvalidOperationException("The preview snapshot has no pipeline.");
            if (snapshot.Status == PipelinePreviewSnapshotStatus.NotReady)
            {
                return View(new PipelinePreviewViewModel
                {
                    PipelineId = pipeline.Id,
                    PipelineName = pipeline.Name,
                    ReadinessProblems = snapshot.Readiness!.Problems
                });
            }

            if (snapshot.Status == PipelinePreviewSnapshotStatus.SourceUnavailable)
            {
                _logger?.LogInformation(
                    "Preview request returned source unavailable for pipeline {PipelineId}; request {RequestId}; process {ProcessId}.",
                    pipeline.Id,
                    HttpContext.TraceIdentifier,
                    Environment.ProcessId);
                Response.StatusCode = StatusCodes.Status410Gone;
                return View(new PipelinePreviewViewModel
                {
                    PipelineId = pipeline.Id,
                    PipelineName = pipeline.Name,
                    FailureMessage = "The inspected source is no longer available or no longer matches this pipeline. Upload and inspect the source again.",
                    RequiresSourceUpload = true
                });
            }

            var source = snapshot.Source
                ?? throw new InvalidOperationException("The ready preview snapshot has no retained source.");
            var preview = await _previewService.PreviewAsync(
                source,
                pipeline,
                cancellationToken);
            return View(PipelinePreviewViewModel.FromPreview(pipeline, preview));
        }
        catch (PipelineNotReadyException exception)
        {
            return View(new PipelinePreviewViewModel
            {
                PipelineId = pipeline?.Id ?? id,
                PipelineName = pipeline?.Name ?? string.Empty,
                ReadinessProblems = exception.Problems
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPreviewSystemFailure(exception))
        {
            _logger?.LogError(
                exception,
                "Preview generation failed for pipeline {PipelineId}.",
                pipeline?.Id ?? id);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return View(new PipelinePreviewViewModel
            {
                PipelineId = pipeline?.Id ?? id,
                PipelineName = pipeline?.Name ?? string.Empty,
                FailureMessage = "The preview could not be generated from the selected source. Review the configuration or upload the source again."
            });
        }
    }

    [HttpPost("/Pipelines/{id:guid}/Execute")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Execute(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        if (_runAdmissionService is null)
        {
            throw new InvalidOperationException("Pipeline execution is not configured.");
        }

        var result = await _runAdmissionService.AdmitAsync(id, cancellationToken);
        switch (result.Status)
        {
            case RunAdmissionStatus.Admitted:
                return RedirectToAction(
                    nameof(RunsController.Progress),
                    "Runs",
                    new { runId = result.RunId!.Value });
            case RunAdmissionStatus.PipelineNotFound:
                return NotFound();
            case RunAdmissionStatus.PipelineNotReady:
                return View("Preview", new PipelinePreviewViewModel
                {
                    PipelineId = result.PipelineId ?? id,
                    PipelineName = result.PipelineName,
                    ReadinessProblems = result.ReadinessProblems
                });
            case RunAdmissionStatus.SourceUnavailable:
                Response.StatusCode = StatusCodes.Status410Gone;
                return View("Preview", new PipelinePreviewViewModel
                {
                    PipelineId = result.PipelineId ?? id,
                    PipelineName = result.PipelineName,
                    FailureMessage = "The inspected source is no longer available or no longer matches this pipeline. Upload and inspect the source again.",
                    RequiresSourceUpload = true
                });
            case RunAdmissionStatus.RunAlreadyActive:
                Response.StatusCode = StatusCodes.Status409Conflict;
                return View("Preview", new PipelinePreviewViewModel
                {
                    PipelineId = result.PipelineId ?? id,
                    PipelineName = result.PipelineName,
                    FailureMessage = "This pipeline already has a queued or running execution. Wait for it to finish before starting another run."
                });
            case RunAdmissionStatus.Failed:
                _logger?.LogError(
                    result.Failure,
                    "ETL run admission failed for pipeline {PipelineId}.",
                    result.PipelineId ?? id);
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return View("Preview", new PipelinePreviewViewModel
                {
                    PipelineId = result.PipelineId ?? id,
                    PipelineName = result.PipelineName,
                    FailureMessage = "The run could not be admitted to background execution. Try again."
                });
            default:
                throw new InvalidOperationException("The run admission status is invalid.");
        }
    }

    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        return View(new PipelineDeleteViewModel
        {
            Id = id,
            Name = pipeline.Name
        });
    }

    [HttpPost]
    [ActionName(nameof(Delete))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        if (!await _pipelineService.DeleteAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (_wizardSourceStore is not null)
        {
            try
            {
                await _wizardSourceStore.RemoveAsync(id, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(
                    exception,
                    "The retained wizard source for deleted pipeline {PipelineId} could not be removed immediately.",
                    id);
            }
        }

        return RedirectToAction(nameof(Index));
    }

    private void PopulateAvailableMappedFields(
        PipelineFormViewModel model,
        PipelineDefinition pipeline)
    {
        try
        {
            _ = _fieldMappingService.Prepare(pipeline);
            model.AvailableMappedFields = pipeline.FieldMappings
                .Where(mapping => mapping.IsIncluded)
                .Select(mapping => mapping.TargetField)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            // An invalid mapping has no effective output fields to select from.
            model.AvailableMappedFields = [];
        }
    }

    private void ValidateDestination(PipelineFormViewModel model)
    {
        if (_targetAccessService is null)
        {
            return;
        }

        var result = _targetAccessService.Validate(new MongoTarget(
            model.DestinationDatabase ?? string.Empty,
            model.DestinationCollection ?? string.Empty));
        if (result.IsAllowed)
        {
            return;
        }

        var errorKey = string.IsNullOrWhiteSpace(model.DestinationDatabase)
            ? nameof(PipelineFormViewModel.DestinationDatabase)
            : string.IsNullOrWhiteSpace(model.DestinationCollection)
                ? nameof(PipelineFormViewModel.DestinationCollection)
                : string.Empty;
        ModelState.AddModelError(errorKey, result.FailureMessage!);
    }

    private static bool IsPreviewSystemFailure(Exception exception) =>
        exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or PostgreSqlConnectionProfileNotFoundException
            or PostgreSqlConnectionAccessException
            or PostgreSqlMetadataObjectNotFoundException
            or MongoSourceAccessException
            or MongoSourceMetadataObjectNotFoundException
            or MongoSourceSchemaChangedException;
}
