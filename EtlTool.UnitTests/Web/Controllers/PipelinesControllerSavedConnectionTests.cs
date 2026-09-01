using EtlTool.Application.Connections;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerSavedConnectionTests
{
    [Fact]
    public async Task InspectSource_SavedPostgreSqlConnectionPersistsReferenceAndDerivesMongoDestination()
    {
        var pipeline = Pipeline();
        var service = new PipelineService(pipeline);
        var connectionId = Guid.NewGuid();
        var result = await new PipelinesController(service, savedMetadataDiscoveryService: new Metadata())
            .InspectSource(pipeline.Id, new SourceUploadViewModel
            {
                SourceType = SourceType.PostgreSql,
                PostgreSqlSavedConnectionId = connectionId,
                PostgreSqlDatabase = "reporting",
                PostgreSqlSchema = "public",
                PostgreSqlTable = "customers"
            }, CancellationToken.None);

        Assert.Equal(nameof(PipelinesController.Mapping), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal(connectionId, service.Updated!.PostgreSqlSource!.SavedConnectionId);
        Assert.Equal(DestinationType.MongoDb, service.Updated.DestinationType);
        Assert.Null(service.Updated.PostgreSqlDestination);
    }

    [Fact]
    public async Task InspectSource_SavedMongoDbConnectionPersistsReferenceAndDerivesPostgreSqlDestination()
    {
        var pipeline = Pipeline();
        var service = new PipelineService(pipeline);
        var connectionId = Guid.NewGuid();
        var result = await new PipelinesController(service, savedMetadataDiscoveryService: new Metadata())
            .InspectSource(pipeline.Id, new SourceUploadViewModel
            {
                SourceType = SourceType.MongoDb,
                MongoDbSavedConnectionId = connectionId,
                MongoDbDatabase = "reporting",
                MongoDbCollection = "customers"
            }, CancellationToken.None);

        Assert.Equal(nameof(PipelinesController.Mapping), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal(connectionId, service.Updated!.MongoDbSource!.SavedConnectionId);
        Assert.Equal(DestinationType.PostgreSql, service.Updated.DestinationType);
        Assert.Null(service.Updated.MongoDbDestinationConnectionId);
    }

    [Fact]
    public async Task Edit_MongoDbDestinationWithoutSavedConnectionDoesNotUseLegacyFallbackOrMutatePipeline()
    {
        var pipeline = Pipeline();
        pipeline.DestinationType = DestinationType.MongoDb;
        pipeline.DestinationDatabase = "original";
        pipeline.DestinationCollection = "customers";
        pipeline.UpsertKeyField = "id";
        var service = new PipelineService(pipeline);
        var controller = new PipelinesController(service, savedMetadataDiscoveryService: new Metadata());
        var result = await controller.Edit(pipeline.Id, new PipelineFormViewModel
        {
            Name = pipeline.Name,
            DestinationType = DestinationType.MongoDb,
            DestinationDatabase = "crafted",
            DestinationCollection = "customers",
            UpsertKeyField = "id"
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.Updated);
        Assert.Contains(controller.ModelState[nameof(PipelineFormViewModel.MongoDbDestinationSavedConnectionId)]!.Errors,
            error => error.ErrorMessage == "Choose a saved MongoDB connection.");
        Assert.Equal(("original", "customers", "id"),
            (pipeline.DestinationDatabase, pipeline.DestinationCollection, pipeline.UpsertKeyField));
    }

    [Fact]
    public async Task Edit_PostgreSqlDestinationWithoutSavedConnectionDoesNotUseLegacyProfileOrMutatePipeline()
    {
        var pipeline = Pipeline();
        pipeline.DestinationType = DestinationType.PostgreSql;
        pipeline.PostgreSqlDestination = new PostgreSqlDestinationOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "original",
            Schema = "public",
            Table = "customers"
        };
        var service = new PipelineService(pipeline);
        var controller = new PipelinesController(service, savedMetadataDiscoveryService: new Metadata());

        var result = await controller.Edit(pipeline.Id, new PipelineFormViewModel
        {
            Name = pipeline.Name,
            DestinationType = DestinationType.PostgreSql,
            PostgreSqlConnectionProfile = "ReportingDb",
            PostgreSqlDatabase = "crafted",
            PostgreSqlSchema = "public",
            PostgreSqlTable = "customers"
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.Updated);
        Assert.Contains(controller.ModelState[nameof(PipelineFormViewModel.PostgreSqlDestinationSavedConnectionId)]!.Errors,
            error => error.ErrorMessage == "Choose a saved PostgreSQL connection.");
        Assert.Equal(("ReportingDb", "original", "public", "customers"),
            (pipeline.PostgreSqlDestination.ConnectionProfile, pipeline.PostgreSqlDestination.Database,
             pipeline.PostgreSqlDestination.Schema, pipeline.PostgreSqlDestination.Table));
    }

    [Fact]
    public async Task Edit_MongoDbDestinationWithWrongProviderSavedConnectionFailsWithoutMutation()
    {
        var pipeline = Pipeline();
        pipeline.DestinationType = DestinationType.MongoDb;
        pipeline.DestinationDatabase = "original";
        pipeline.DestinationCollection = "customers";
        pipeline.UpsertKeyField = "id";
        var service = new PipelineService(pipeline);
        var controller = new PipelinesController(service,
            savedMetadataDiscoveryService: new Metadata { Failure = new SavedConnectionResolutionException() });

        var result = await controller.Edit(pipeline.Id, new PipelineFormViewModel
        {
            Name = pipeline.Name,
            DestinationType = DestinationType.MongoDb,
            MongoDbDestinationSavedConnectionId = Guid.NewGuid(),
            DestinationDatabase = "crafted",
            DestinationCollection = "customers",
            UpsertKeyField = "id"
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.Updated);
        Assert.Equal(("original", "customers", "id"),
            (pipeline.DestinationDatabase, pipeline.DestinationCollection, pipeline.UpsertKeyField));
    }

    [Fact]
    public async Task Edit_PostgreSqlDestinationWithWrongProviderSavedConnectionFailsWithoutMutation()
    {
        var pipeline = Pipeline();
        var previousDestination = pipeline.PostgreSqlDestination!;
        var service = new PipelineService(pipeline);
        var controller = new PipelinesController(service,
            savedMetadataDiscoveryService: new Metadata { Failure = new SavedConnectionResolutionException() });

        var result = await controller.Edit(pipeline.Id, new PipelineFormViewModel
        {
            Name = pipeline.Name,
            DestinationType = DestinationType.PostgreSql,
            PostgreSqlDestinationSavedConnectionId = Guid.NewGuid(),
            PostgreSqlDatabase = "crafted",
            PostgreSqlSchema = "public",
            PostgreSqlTable = "customers"
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.Updated);
        Assert.Same(previousDestination, pipeline.PostgreSqlDestination);
        var persistedDestination = Assert.IsType<PostgreSqlDestinationOptions>(pipeline.PostgreSqlDestination);
        Assert.Equal(("legacy", "db", "public", "old"),
            (persistedDestination.ConnectionProfile, persistedDestination.Database,
             persistedDestination.Schema, persistedDestination.Table));
    }

    [Fact]
    public async Task PostgreSqlDatabases_ReturnsOnlySafeLogicalMetadata()
    {
        var result = await new PipelinesController(new PipelineService(Pipeline()),
                savedMetadataDiscoveryService: new Metadata())
            .PostgreSqlDatabases(Guid.NewGuid(), CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result);
        var payload = JsonSerializer.Serialize(json.Value);
        Assert.Contains("reporting", payload);
        Assert.DoesNotContain("connection", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MongoDbCollections_RejectsMissingConnectionIdSafely()
    {
        var result = await new PipelinesController(new PipelineService(Pipeline()),
                savedMetadataDiscoveryService: new Metadata())
            .MongoDbCollections(Guid.Empty, "reporting", CancellationToken.None);

        var error = Assert.IsType<BadRequestObjectResult>(result);
        Assert.DoesNotContain("password", JsonSerializer.Serialize(error.Value), StringComparison.OrdinalIgnoreCase);
    }

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(), Name = "Pipeline", SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions { FirstRowIsHeader = true },
        ExpectedSchema = [], FieldMappings = [], DestinationType = DestinationType.PostgreSql,
        PostgreSqlDestination = new PostgreSqlDestinationOptions { ConnectionProfile = "legacy", Database = "db", Schema = "public", Table = "old" }
    };

    private sealed class PipelineService(PipelineDefinition pipeline) : IPipelineService
    {
        public PipelineDefinition? Updated { get; private set; }
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);
        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken) { Updated = value; return Task.FromResult(true); }
        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Metadata : ISavedConnectionMetadataDiscoveryService
    {
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverPostgreSqlDatabasesAsync(Guid id, CancellationToken token) => Result<IReadOnlyList<PostgreSqlDatabaseMetadata>>([new("reporting")]);
        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverPostgreSqlSchemasAsync(Guid id, string database, CancellationToken token) => Result<IReadOnlyList<PostgreSqlSchemaMetadata>>([new("public")]);
        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverPostgreSqlTablesAsync(Guid id, string database, string schema, CancellationToken token) => Result<IReadOnlyList<PostgreSqlTableMetadata>>([new("customers")]);
        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverPostgreSqlColumnsAsync(Guid id, string database, string schema, string table, CancellationToken token) => Result<IReadOnlyList<PostgreSqlColumnMetadata>>([new("id", "integer", false, 1)]);
        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverPostgreSqlKeyConstraintsAsync(Guid id, string database, string schema, string table, CancellationToken token) => Result<IReadOnlyList<PostgreSqlKeyConstraintMetadata>>([]);
        public Task EnsurePostgreSqlDestinationAccessibleAsync(Guid id, string database, string schema, string table, CancellationToken token) => Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        public Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverMongoDatabasesAsync(Guid id, CancellationToken token) => Result<IReadOnlyList<MongoDatabaseMetadata>>([new("reporting")]);
        public Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverMongoCollectionsAsync(Guid id, string database, CancellationToken token) => Result<IReadOnlyList<MongoCollectionMetadata>>([new("customers")]);
        public Task<IReadOnlyList<SourceFieldDefinition>> InferMongoSchemaAsync(Guid id, string database, string collection, CancellationToken token) => Result<IReadOnlyList<SourceFieldDefinition>>([new() { Name = "id", DataType = SourceFieldType.Integer }]);

        private Task<T> Result<T>(T value) => Failure is null
            ? Task.FromResult(value)
            : Task.FromException<T>(Failure);
    }
}
