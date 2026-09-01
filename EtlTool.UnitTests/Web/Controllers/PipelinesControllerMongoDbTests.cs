using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerMongoDbTests
{
    [Fact]
    public async Task InspectSource_MongoDbPersistsSourceIdentitySchemaAndMappingsWithoutChangingDestination()
    {
        var pipeline = Pipeline();
        var service = new RecordingPipelineService(pipeline);
        var discovery = new RecordingMongoMetadataDiscoveryService();
        var inference = new RecordingMongoSchemaInferenceService
        {
            Schema = [Field("Id", SourceFieldType.Integer), Field("Name", SourceFieldType.String)]
        };
        var controller = CreateController(service, discovery, inference);

        var result = await controller.InspectSource(pipeline.Id, new SourceUploadViewModel
        {
            SourceType = SourceType.MongoDb,
            MongoDbDatabase = "reporting",
            MongoDbCollection = "customers",
            LoadedMongoDbDatabase = "reporting"
        }, CancellationToken.None, mongoDbAction: "configure");

        var mappingModel = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.True(mappingModel.SchemaDifference!.RequiresRemapping);
        Assert.Equal("Legacy", Assert.Single(mappingModel.SchemaDifference.MissingFields).Name);
        Assert.Equal("Name", Assert.Single(mappingModel.SchemaDifference.NewFields).Name);
        var saved = Assert.IsType<PipelineDefinition>(service.UpdatedPipeline);
        Assert.Equal(SourceType.MongoDb, saved.SourceType);
        Assert.True(saved.RequiresRemapping);
        Assert.Equal(("reporting", "customers"),
            (saved.MongoDbSource!.Database, saved.MongoDbSource.Collection));
        Assert.Null(saved.PostgreSqlSource);
        Assert.Equal(("destination_db", "destination_rows", "id"),
            (saved.DestinationDatabase, saved.DestinationCollection, saved.UpsertKeyField));
        Assert.Equal(["Id", "Name"], saved.ExpectedSchema.Select(field => field.Name));
        Assert.Collection(saved.FieldMappings,
            mapping => Assert.Equal(("Id", "id", true),
                (mapping.SourceField, mapping.TargetField, mapping.IsIncluded)),
            mapping => Assert.Equal(("Name", string.Empty, false),
                (mapping.SourceField, mapping.TargetField, mapping.IsIncluded)));
        Assert.Equal(("reporting", "customers"), inference.LastRequest);
    }

    [Fact]
    public async Task InspectSource_MongoDbRejectsStaleCollectionWithoutSaving()
    {
        var pipeline = Pipeline();
        var service = new RecordingPipelineService(pipeline);
        var controller = CreateController(
            service,
            new RecordingMongoMetadataDiscoveryService(),
            new RecordingMongoSchemaInferenceService());

        var result = await controller.InspectSource(pipeline.Id, new SourceUploadViewModel
        {
            SourceType = SourceType.MongoDb,
            MongoDbDatabase = "reporting",
            MongoDbCollection = "removed",
            LoadedMongoDbDatabase = "reporting"
        }, CancellationToken.None, mongoDbAction: "configure");

        var model = Assert.IsType<SourceUploadViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(service.UpdatedPipeline);
        Assert.Null(model.MongoDbCollection);
        Assert.Contains(controller.ModelState[nameof(model.MongoDbCollection)]!.Errors,
            error => error.ErrorMessage.Contains("no longer available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectSource_MongoDbNewSourceRedirectsToSharedMappingFlow()
    {
        var pipeline = Pipeline();
        pipeline.ExpectedSchema = [];
        pipeline.FieldMappings = [];
        var service = new RecordingPipelineService(pipeline);
        var controller = CreateController(
            service,
            new RecordingMongoMetadataDiscoveryService(),
            new RecordingMongoSchemaInferenceService());

        var result = await controller.InspectSource(pipeline.Id, new SourceUploadViewModel
        {
            SourceType = SourceType.MongoDb,
            MongoDbDatabase = "reporting",
            MongoDbCollection = "customers",
            LoadedMongoDbDatabase = "reporting"
        }, CancellationToken.None, mongoDbAction: "configure");

        Assert.Equal(nameof(PipelinesController.Mapping),
            Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal(SourceType.MongoDb, service.UpdatedPipeline!.SourceType);
    }

    [Fact]
    public async Task InspectSource_MongoDbAccessFailureReturnsSafeErrorWithoutSaving()
    {
        var pipeline = Pipeline();
        var service = new RecordingPipelineService(pipeline);
        var controller = CreateController(
            service,
            new RecordingMongoMetadataDiscoveryService { DiscoveryException = new MongoSourceAccessException() },
            new RecordingMongoSchemaInferenceService());

        var result = await controller.InspectSource(pipeline.Id, new SourceUploadViewModel
        {
            SourceType = SourceType.MongoDb,
            MongoDbDatabase = "reporting",
            MongoDbCollection = "customers",
            LoadedMongoDbDatabase = "reporting"
        }, CancellationToken.None, mongoDbAction: "configure");

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.UpdatedPipeline);
        var errors = controller.ModelState[string.Empty]!.Errors.Select(error => error.ErrorMessage).ToArray();
        Assert.Contains(errors, error => error.Contains("could not be accessed", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("mongodb://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Mapping_MongoDbSourcePreservesSourceAndDestinationConfiguration()
    {
        var pipeline = Pipeline();
        pipeline.SourceType = SourceType.MongoDb;
        pipeline.PostgreSqlSource = null;
        pipeline.MongoDbSource = new MongoDbSourceOptions
        {
            Database = "reporting",
            Collection = "customers"
        };
        pipeline.RequiresRemapping = true;
        var service = new RecordingPipelineService(pipeline);
        var controller = new PipelinesController(service);

        var result = await controller.Mapping(pipeline.Id, new FieldMappingViewModel
        {
            Fields =
            [
                new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "id", IsIncluded = true },
                new FieldMappingFieldViewModel { SourceField = "Legacy", TargetField = "legacy", IsIncluded = true }
            ]
        }, CancellationToken.None);

        Assert.True(Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model).IsSaved);
        var saved = Assert.IsType<PipelineDefinition>(service.UpdatedPipeline);
        Assert.Equal(("reporting", "customers"),
            (saved.MongoDbSource!.Database, saved.MongoDbSource.Collection));
        Assert.Equal(("destination_db", "destination_rows", "id"),
            (saved.DestinationDatabase, saved.DestinationCollection, saved.UpsertKeyField));
        Assert.False(saved.RequiresRemapping);
    }

    [Fact]
    public async Task InspectSource_MongoDbSuccessfulFileSwitchRetiresThePriorWizardSource()
    {
        var pipeline = Pipeline();
        pipeline.SourceType = SourceType.Csv;
        pipeline.PostgreSqlSource = null;
        pipeline.ExpectedSchema = [];
        pipeline.FieldMappings = [];
        var service = new RecordingPipelineService(pipeline);
        var sourceStore = new RecordingWizardSourceStore();
        var controller = CreateController(
            service,
            new RecordingMongoMetadataDiscoveryService(),
            new RecordingMongoSchemaInferenceService(),
            sourceStore);

        await controller.InspectSource(pipeline.Id, MongoDbModel(), CancellationToken.None, mongoDbAction: "configure");

        Assert.Equal([pipeline.Id], sourceStore.RetiredPipelineIds);
    }

    [Fact]
    public async Task InspectSource_MongoDbFailedConfigurationDoesNotRetireThePriorWizardSource()
    {
        var pipeline = Pipeline();
        pipeline.SourceType = SourceType.Csv;
        pipeline.PostgreSqlSource = null;
        var service = new RecordingPipelineService(pipeline);
        var sourceStore = new RecordingWizardSourceStore();
        var controller = CreateController(
            service,
            new RecordingMongoMetadataDiscoveryService { DiscoveryException = new MongoSourceAccessException() },
            new RecordingMongoSchemaInferenceService(),
            sourceStore);

        await controller.InspectSource(pipeline.Id, MongoDbModel(), CancellationToken.None, mongoDbAction: "configure");

        Assert.Empty(sourceStore.RetiredPipelineIds);
    }

    private static PipelinesController CreateController(
        RecordingPipelineService service,
        IMongoSourceMetadataDiscoveryService discovery,
        IMongoSourceSchemaInferenceService inference,
        IWizardSourceStore? sourceStore = null) => new(
            service,
            wizardSourceStore: sourceStore,
            mongoSourceMetadataDiscoveryService: discovery,
            mongoSourceSchemaInferenceService: inference);

    private static SourceUploadViewModel MongoDbModel() => new()
    {
        SourceType = SourceType.MongoDb,
        MongoDbDatabase = "reporting",
        MongoDbCollection = "customers",
        LoadedMongoDbDatabase = "reporting"
    };

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "MongoDB pipeline",
        SourceType = SourceType.PostgreSql,
        PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb", Database = "reporting", Schema = "public", Table = "legacy"
        },
        SourceOptions = new SourceOptions { FirstRowIsHeader = true },
        ExpectedSchema = [Field("Id", SourceFieldType.Integer), Field("Legacy", SourceFieldType.String)],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true },
            new FieldMapping { SourceField = "Legacy", TargetField = "legacy", IsIncluded = true }
        ],
        DestinationDatabase = "destination_db",
        DestinationCollection = "destination_rows",
        UpsertKeyField = "id"
    };

    private static SourceFieldDefinition Field(string name, SourceFieldType type) => new()
    {
        Name = name,
        DataType = type
    };

    private sealed class RecordingPipelineService(PipelineDefinition pipeline) : IPipelineService
    {
        private PipelineDefinition _pipeline = pipeline;

        public PipelineDefinition? UpdatedPipeline { get; private set; }

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == _pipeline.Id ? _pipeline : null);

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken)
        {
            UpdatedPipeline = value;
            _pipeline = value;
            return Task.FromResult(id == _pipeline.Id);
        }

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingMongoMetadataDiscoveryService : IMongoSourceMetadataDiscoveryService
    {
        public Exception? DiscoveryException { get; init; }

        public Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverDatabasesAsync(CancellationToken cancellationToken) =>
            DiscoveryException is null
                ? Task.FromResult<IReadOnlyList<MongoDatabaseMetadata>>([new("reporting")])
                : Task.FromException<IReadOnlyList<MongoDatabaseMetadata>>(DiscoveryException);

        public Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverCollectionsAsync(
            string database,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MongoCollectionMetadata>>([new("customers")]);
    }

    private sealed class RecordingMongoSchemaInferenceService : IMongoSourceSchemaInferenceService
    {
        public IReadOnlyList<SourceFieldDefinition> Schema { get; init; } = [Field("Id", SourceFieldType.Integer)];

        public (string Database, string Collection)? LastRequest { get; private set; }

        public Task<IReadOnlyList<SourceFieldDefinition>> InferAsync(
            MongoDbSourceOptions source,
            CancellationToken cancellationToken)
        {
            LastRequest = (source.Database, source.Collection);
            return Task.FromResult(Schema);
        }
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
