using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerPostgreSqlTests
{
    [Fact]
    public async Task Source_GetPresentsOnlyLogicalProfileNamesForAPostgreSqlPipeline()
    {
        var id = Guid.NewGuid();
        var pipeline = Pipeline(id);
        pipeline.SourceType = SourceType.PostgreSql;
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };
        var controller = CreateController(
            new RecordingPipelineService { Pipeline = pipeline },
            new RecordingPostgreSqlMetadataDiscoveryService(),
            new RecordingProfileCatalog("ReportingDb"));

        var result = await controller.Source(id, CancellationToken.None);

        var model = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(SourceType.PostgreSql, model.SourceType);
        Assert.Equal(["ReportingDb"], model.PostgreSqlConnectionProfiles);
        Assert.DoesNotContain("Password", string.Join(',', model.PostgreSqlConnectionProfiles), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ReportingDb", model.PostgreSqlConnectionProfile);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlValidSelectionPersistsLogicalMetadataAndRedirectsToMapping()
    {
        var id = Guid.NewGuid();
        var pipeline = Pipeline(id);
        pipeline.DestinationType = DestinationType.PostgreSql;
        pipeline.PostgreSqlDestination = new PostgreSqlDestinationOptions
        {
            ConnectionProfile = "WarehouseDb",
            Database = "warehouse",
            Schema = "import",
            Table = "customers"
        };
        pipeline.DestinationDatabase = "stale";
        pipeline.DestinationCollection = "stale";
        pipeline.UpsertKeyField = "stale";
        var service = new RecordingPipelineService { Pipeline = pipeline };
        var discovery = new RecordingPostgreSqlMetadataDiscoveryService();
        var controller = CreateController(service, discovery, new RecordingProfileCatalog("ReportingDb"));

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        {
            SourceType = SourceType.PostgreSql,
            PostgreSqlConnectionProfile = "ReportingDb",
            PostgreSqlDatabase = "reporting",
            PostgreSqlSchema = "public",
            PostgreSqlTable = "customers",
            LoadedPostgreSqlConnectionProfile = "ReportingDb",
            LoadedPostgreSqlDatabase = "reporting",
            LoadedPostgreSqlSchema = "public"
        }, CancellationToken.None, "configure");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PipelinesController.Mapping), redirect.ActionName);
        var saved = Assert.IsType<PipelineDefinition>(service.UpdatedPipeline);
        Assert.Equal(SourceType.PostgreSql, saved.SourceType);
        Assert.Equal("ReportingDb", saved.PostgreSqlSource!.ConnectionProfile);
        Assert.Equal("reporting", saved.PostgreSqlSource.Database);
        Assert.Equal("public", saved.PostgreSqlSource.Schema);
        Assert.Equal("customers", saved.PostgreSqlSource.Table);
        Assert.Equal(DestinationType.MongoDb, saved.DestinationType);
        Assert.Null(saved.PostgreSqlDestination);
        Assert.Equal(string.Empty, saved.DestinationDatabase);
        Assert.Equal(string.Empty, saved.DestinationCollection);
        Assert.Equal(string.Empty, saved.UpsertKeyField);
        Assert.Collection(saved.ExpectedSchema,
            field => Assert.Equal(("Id", SourceFieldType.Integer), (field.Name, field.DataType)),
            field => Assert.Equal(("Name", SourceFieldType.String), (field.Name, field.DataType)));
        Assert.Equal(1, discovery.DiscoverColumnsCallCount);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlSchemaChangeRequiresMappingSaveBeforeTheGateClears()
    {
        var id = Guid.NewGuid();
        var pipeline = Pipeline(id);
        pipeline.SourceType = SourceType.PostgreSql;
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb", Database = "reporting", Schema = "public", Table = "customers"
        };
        pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }];
        pipeline.FieldMappings = [new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true }];
        var service = new RecordingPipelineService { Pipeline = pipeline };
        var controller = CreateController(
            service,
            new RecordingPostgreSqlMetadataDiscoveryService(),
            new RecordingProfileCatalog("ReportingDb"));

        var result = await controller.InspectSource(id, ValidPostgreSqlModel(), CancellationToken.None, "configure");

        Assert.True(Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model)
            .SchemaDifference!.RequiresRemapping);
        Assert.True(service.UpdatedPipeline!.RequiresRemapping);

        var mapping = await controller.Mapping(id, new FieldMappingViewModel
        {
            Fields =
            [
                new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "id", IsIncluded = true },
                new FieldMappingFieldViewModel { SourceField = "Name", TargetField = "", IsIncluded = false }
            ]
        }, CancellationToken.None);

        Assert.True(Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(mapping).Model).IsSaved);
        Assert.False(service.UpdatedPipeline!.RequiresRemapping);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlSuccessfulFileSwitchRetiresThePriorWizardSource()
    {
        var id = Guid.NewGuid();
        var pipeline = Pipeline(id);
        pipeline.SourceType = SourceType.Csv;
        var service = new RecordingPipelineService { Pipeline = pipeline };
        var sourceStore = new RecordingWizardSourceStore();
        var controller = CreateController(
            service,
            new RecordingPostgreSqlMetadataDiscoveryService(),
            new RecordingProfileCatalog("ReportingDb"),
            sourceStore);

        await controller.InspectSource(id, ValidPostgreSqlModel(), CancellationToken.None, "configure");

        Assert.Equal([id], sourceStore.RetiredPipelineIds);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlFailedConfigurationDoesNotRetireThePriorWizardSource()
    {
        var id = Guid.NewGuid();
        var pipeline = Pipeline(id);
        pipeline.SourceType = SourceType.Csv;
        var service = new RecordingPipelineService { Pipeline = pipeline };
        var sourceStore = new RecordingWizardSourceStore();
        var controller = CreateController(
            service,
            new RecordingPostgreSqlMetadataDiscoveryService
            {
                DiscoveryException = new PostgreSqlConnectionAccessException()
            },
            new RecordingProfileCatalog("ReportingDb"),
            sourceStore);

        await controller.InspectSource(id, ValidPostgreSqlModel(), CancellationToken.None, "configure");

        Assert.Empty(sourceStore.RetiredPipelineIds);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlRejectsStaleDatabaseWithoutSaving()
    {
        var id = Guid.NewGuid();
        var service = new RecordingPipelineService { Pipeline = Pipeline(id) };
        var controller = CreateController(
            service,
            new RecordingPostgreSqlMetadataDiscoveryService(),
            new RecordingProfileCatalog("ReportingDb"));

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        {
            SourceType = SourceType.PostgreSql,
            PostgreSqlConnectionProfile = "ReportingDb",
            PostgreSqlDatabase = "removed_database",
            LoadedPostgreSqlConnectionProfile = "ReportingDb",
            LoadedPostgreSqlDatabase = "removed_database"
        }, CancellationToken.None, "configure");

        var model = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(service.UpdatedPipeline);
        Assert.Null(model.PostgreSqlDatabase);
        Assert.Contains(controller.ModelState[nameof(model.PostgreSqlDatabase)]!.Errors,
            error => error.ErrorMessage.Contains("no longer available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectSource_PostgreSqlProfileChangeClearsDownstreamSelections()
    {
        var id = Guid.NewGuid();
        var controller = CreateController(
            new RecordingPipelineService { Pipeline = Pipeline(id) },
            new RecordingPostgreSqlMetadataDiscoveryService(),
            new RecordingProfileCatalog("ReportingDb", "StagingDb"));

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        {
            SourceType = SourceType.PostgreSql,
            PostgreSqlConnectionProfile = "StagingDb",
            PostgreSqlDatabase = "reporting",
            PostgreSqlSchema = "public",
            PostgreSqlTable = "customers",
            LoadedPostgreSqlConnectionProfile = "ReportingDb",
            LoadedPostgreSqlDatabase = "reporting",
            LoadedPostgreSqlSchema = "public"
        }, CancellationToken.None, "refresh");

        var model = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("StagingDb", model.PostgreSqlConnectionProfile);
        Assert.Null(model.PostgreSqlDatabase);
        Assert.Null(model.PostgreSqlSchema);
        Assert.Null(model.PostgreSqlTable);
        Assert.Equal("StagingDb", model.LoadedPostgreSqlConnectionProfile);
        Assert.Equal(["reporting"], model.PostgreSqlDatabases);
    }

    [Theory]
    [InlineData("schema", "removed_schema")]
    [InlineData("table", "removed_table")]
    public async Task InspectSource_PostgreSqlRejectsStaleSchemaOrTableWithoutSaving(
        string selectionType,
        string staleValue)
    {
        var id = Guid.NewGuid();
        var service = new RecordingPipelineService { Pipeline = Pipeline(id) };
        var controller = CreateController(
            service,
            new RecordingPostgreSqlMetadataDiscoveryService(),
            new RecordingProfileCatalog("ReportingDb"));
        var model = ValidPostgreSqlModel();
        if (selectionType == "schema")
        {
            model.PostgreSqlSchema = staleValue;
            model.LoadedPostgreSqlSchema = staleValue;
        }
        else
        {
            model.PostgreSqlTable = staleValue;
        }

        var result = await controller.InspectSource(id, model, CancellationToken.None, "configure");

        var returned = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(service.UpdatedPipeline);
        var propertyName = selectionType == "schema"
            ? nameof(model.PostgreSqlSchema)
            : nameof(model.PostgreSqlTable);
        Assert.Contains(controller.ModelState[propertyName]!.Errors,
            error => error.ErrorMessage.Contains("no longer available", StringComparison.Ordinal));
        Assert.Null(selectionType == "schema" ? returned.PostgreSqlSchema : returned.PostgreSqlTable);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlDatabaseChangeClearsSchemaAndTable()
    {
        var id = Guid.NewGuid();
        var controller = CreateController(
            new RecordingPipelineService { Pipeline = Pipeline(id) },
            new RecordingPostgreSqlMetadataDiscoveryService
            {
                Databases = [new("reporting"), new("archive")]
            },
            new RecordingProfileCatalog("ReportingDb"));
        var model = ValidPostgreSqlModel();
        model.PostgreSqlDatabase = "archive";

        var result = await controller.InspectSource(id, model, CancellationToken.None, "refresh");

        var returned = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("archive", returned.PostgreSqlDatabase);
        Assert.Null(returned.PostgreSqlSchema);
        Assert.Null(returned.PostgreSqlTable);
        Assert.Equal("archive", returned.LoadedPostgreSqlDatabase);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlAccessFailureReturnsSafeErrorWithoutSaving()
    {
        var id = Guid.NewGuid();
        var service = new RecordingPipelineService { Pipeline = Pipeline(id) };
        var controller = CreateController(
            service,
            new RecordingPostgreSqlMetadataDiscoveryService
            {
                DiscoveryException = new PostgreSqlConnectionAccessException()
            },
            new RecordingProfileCatalog("ReportingDb"));

        var result = await controller.InspectSource(id, ValidPostgreSqlModel(), CancellationToken.None, "configure");

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.UpdatedPipeline);
        var errors = controller.ModelState[string.Empty]!.Errors.Select(error => error.ErrorMessage).ToArray();
        Assert.Contains(errors, error => error.Contains("could not be accessed", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InspectSource_CsvReconfigurationClearsPostgreSqlMetadata()
    {
        var id = Guid.NewGuid();
        var pipeline = Pipeline(id);
        pipeline.SourceType = SourceType.PostgreSql;
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb", Database = "reporting", Schema = "public", Table = "customers"
        };
        var service = new RecordingPipelineService { Pipeline = pipeline };
        var controller = new PipelinesController(service, new RecordingSourceInspectionService());
        var file = new FormFile(new MemoryStream("Id\n1"u8.ToArray()), 0, 4, "SourceFile", "customers.csv");

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        {
            SourceType = SourceType.Csv,
            Delimiter = CsvDelimiter.Comma,
            SourceFile = file
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        var saved = Assert.IsType<PipelineDefinition>(service.UpdatedPipeline);
        Assert.Equal(SourceType.Csv, saved.SourceType);
        Assert.Null(saved.PostgreSqlSource);
    }

    [Fact]
    public async Task InspectSource_PostgreSqlUnsupportedColumnDoesNotSavePartialSchema()
    {
        var id = Guid.NewGuid();
        var service = new RecordingPipelineService { Pipeline = Pipeline(id) };
        var discovery = new RecordingPostgreSqlMetadataDiscoveryService
        {
            Columns = [new PostgreSqlColumnMetadata("Attributes", "jsonb", true, 1)]
        };
        var controller = CreateController(service, discovery, new RecordingProfileCatalog("ReportingDb"));

        var result = await controller.InspectSource(id, ValidPostgreSqlModel(), CancellationToken.None, "configure");

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.UpdatedPipeline);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors,
            error => error.ErrorMessage.Contains("unsupported type", StringComparison.OrdinalIgnoreCase));
    }

    private static PipelinesController CreateController(
        RecordingPipelineService pipelineService,
        RecordingPostgreSqlMetadataDiscoveryService discovery,
        RecordingProfileCatalog profiles,
        IWizardSourceStore? sourceStore = null) => new(
            pipelineService,
            wizardSourceStore: sourceStore,
            postgreSqlMetadataDiscoveryService: discovery,
            postgreSqlConnectionProfileCatalog: profiles);

    private static PipelineDefinition Pipeline(Guid id) => new()
    {
        Id = id,
        Name = "PostgreSQL pipeline",
        SourceOptions = new SourceOptions { FirstRowIsHeader = true }
    };

    private static SourceUploadViewModel ValidPostgreSqlModel() => new()
    {
        SourceType = SourceType.PostgreSql,
        PostgreSqlConnectionProfile = "ReportingDb",
        PostgreSqlDatabase = "reporting",
        PostgreSqlSchema = "public",
        PostgreSqlTable = "customers",
        LoadedPostgreSqlConnectionProfile = "ReportingDb",
        LoadedPostgreSqlDatabase = "reporting",
        LoadedPostgreSqlSchema = "public"
    };

    private sealed class RecordingProfileCatalog(params string[] names) : IPostgreSqlConnectionProfileCatalog
    {
        public IReadOnlyList<string> GetProfileNames() => names;
    }

    private sealed class RecordingPostgreSqlMetadataDiscoveryService : IPostgreSqlMetadataDiscoveryService
    {
        public IReadOnlyList<PostgreSqlDatabaseMetadata> Databases { get; init; } = [new("reporting")];
        public IReadOnlyList<PostgreSqlColumnMetadata> Columns { get; init; } =
        [
            new PostgreSqlColumnMetadata("Id", "integer", false, 1),
            new PostgreSqlColumnMetadata("Name", "varchar(100)", true, 2)
        ];

        public int DiscoverColumnsCallCount { get; private set; }
        public Exception? DiscoveryException { get; init; }

        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(
            string connectionProfile,
            CancellationToken cancellationToken) =>
            DiscoveryException is null
                ? Task.FromResult(Databases)
                : Task.FromException<IReadOnlyList<PostgreSqlDatabaseMetadata>>(DiscoveryException);

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PostgreSqlSchemaMetadata>>([new("public")]);

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(
            string connectionProfile,
            string database,
            string schema,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PostgreSqlTableMetadata>>([new("customers")]);

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            DiscoverColumnsCallCount++;
            return Task.FromResult(Columns);
        }

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PostgreSqlKeyConstraintMetadata>>([]);
    }

    private sealed class RecordingSourceInspectionService : ISourceInspectionService
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
                Columns = ["Id"],
                DetectedSchema = [new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }],
                SourceReferenceId = Guid.NewGuid()
            });

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
            CancellationToken cancellationToken) => Task.FromResult<PendingSourceInspection?>(null);
    }

    private sealed class RecordingPipelineService : IPipelineService
    {
        public PipelineDefinition? Pipeline { get; set; }
        public PipelineDefinition? UpdatedPipeline { get; private set; }

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Pipeline?.Id == id ? Pipeline : null);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PipelineDefinition>>([]);

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition pipeline, CancellationToken cancellationToken)
        {
            UpdatedPipeline = pipeline;
            Pipeline = pipeline;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RecordingWizardSourceStore : IWizardSourceStore
    {
        public List<Guid> RetiredPipelineIds { get; } = [];

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken)
        {
            RetiredPipelineIds.Add(pipelineId);
            return Task.CompletedTask;
        }

        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
