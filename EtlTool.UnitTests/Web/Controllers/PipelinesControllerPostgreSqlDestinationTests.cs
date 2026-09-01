using EtlTool.Application.Connections;
using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerPostgreSqlDestinationTests
{
    private static readonly Guid SavedConnectionId = Guid.Parse("37a127a1-1cf1-4381-9827-922f71261621");

    [Fact]
    public async Task Edit_PostgreSqlDestinationPersistsValidatedIdentityMappingAndUpsertKey()
    {
        var pipeline = Pipeline();
        var service = new PipelineServiceStub(pipeline);
        var result = await CreateController(service).Edit(
            pipeline.Id, Model(), CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PipelinesController.Edit), redirect.ActionName);
        Assert.Equal(pipeline.Id, redirect.RouteValues!["id"]);
        Assert.Equal("destination", redirect.Fragment);
        var saved = Assert.IsType<PipelineDefinition>(service.Updated);
        Assert.Equal(DestinationType.PostgreSql, saved.DestinationType);
        Assert.Empty(saved.DestinationDatabase);
        Assert.Empty(saved.DestinationCollection);
        Assert.Equal(SavedConnectionId, saved.PostgreSqlDestination!.SavedConnectionId);
        Assert.Empty(saved.PostgreSqlDestination.ConnectionProfile);
        Assert.Equal("warehouse", saved.PostgreSqlDestination.Database);
        Assert.Equal("import", saved.PostgreSqlDestination.Schema);
        Assert.Equal("customers", saved.PostgreSqlDestination.Table);
        Assert.Equal(("email", "email"),
            (Assert.Single(saved.PostgreSqlDestination.ColumnMappings).OutputField,
             saved.PostgreSqlDestination.ColumnMappings[0].DestinationColumn));
        Assert.Equal("email", saved.PostgreSqlDestination.UpsertKeyColumn);
        Assert.Equal("email", saved.UpsertKeyField);
    }

    [Fact]
    public async Task Edit_PostgreSqlDestinationRejectsStaleTableAndDoesNotSave()
    {
        var pipeline = Pipeline();
        var service = new PipelineServiceStub(pipeline);
        var model = Model();
        model.PostgreSqlTable = "removed";

        var result = await CreateController(service).Edit(pipeline.Id, model, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.Updated);
    }

    [Fact]
    public async Task Edit_PostgreSqlDestinationAccessFailureIsSafeAndDoesNotSave()
    {
        var pipeline = Pipeline();
        var service = new PipelineServiceStub(pipeline);
        var controller = CreateController(service, new Discovery { ThrowAccess = true });

        var result = await controller.Edit(pipeline.Id, Model(), CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.Updated);
        var errors = controller.ModelState[string.Empty]!.Errors.Select(error => error.ErrorMessage).ToArray();
        Assert.Contains(errors, error => error.Contains("could not be accessed", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, error => error.Contains("connection string", StringComparison.OrdinalIgnoreCase));
    }

    private static PipelinesController CreateController(
        PipelineServiceStub service,
        Discovery? discovery = null)
    {
        var effectiveDiscovery = discovery ?? new Discovery();
        return new PipelinesController(
            service,
            savedMetadataDiscoveryService: new SavedDiscovery(effectiveDiscovery));
    }

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Customer import",
        SourceType = SourceType.Csv,
        ExpectedSchema = [new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }],
        FieldMappings = [new FieldMapping { SourceField = "Email", TargetField = "email", IsIncluded = true }]
    };

    private static PipelineFormViewModel Model() => new()
    {
        Name = "Customer import",
        DestinationType = DestinationType.PostgreSql,
        PostgreSqlDestinationSavedConnectionId = SavedConnectionId,
        PostgreSqlConnectionProfile = "WarehouseDb",
        PostgreSqlDatabase = "warehouse",
        PostgreSqlSchema = "import",
        PostgreSqlTable = "customers",
        LoadedPostgreSqlConnectionProfile = "WarehouseDb",
        LoadedPostgreSqlDatabase = "warehouse",
        LoadedPostgreSqlSchema = "import",
        PostgreSqlColumnMappings =
        [
            new PostgreSqlDestinationMappingViewModel { OutputField = "email", DestinationColumn = "email" }
        ],
        PostgreSqlUpsertKeyColumn = "email"
    };

    private sealed class PipelineServiceStub(PipelineDefinition pipeline) : IPipelineService
    {
        public PipelineDefinition? Updated { get; private set; }

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition value, CancellationToken cancellationToken) =>
            Task.FromResult(value);

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineDefinition?>(id == pipeline.Id ? pipeline : null);

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PipelineDefinition>>([]);

        public Task<bool> UpdateAsync(Guid id, PipelineDefinition value, CancellationToken cancellationToken)
        {
            Updated = value;
            return Task.FromResult(id == pipeline.Id);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class Profiles : IPostgreSqlConnectionProfileCatalog
    {
        public IReadOnlyList<string> GetProfileNames() => ["WarehouseDb"];
    }

    private sealed class Discovery : IPostgreSqlMetadataDiscoveryService, IPostgreSqlDestinationAccessService
    {
        public bool ThrowAccess { get; init; }

        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(
            string connectionProfile, CancellationToken cancellationToken) => Access(
                [new PostgreSqlDatabaseMetadata("warehouse")]);

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(
            string connectionProfile, string database, CancellationToken cancellationToken) => Access(
                [new PostgreSqlSchemaMetadata("import")]);

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(
            string connectionProfile, string database, string schema, CancellationToken cancellationToken) => Access(
                [new PostgreSqlTableMetadata("customers")]);

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
            string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken) => Access(
            [new PostgreSqlColumnMetadata("email", "text", false, 1)]);

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
            string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken) => Access(
            [new PostgreSqlKeyConstraintMetadata(
                "customers_email_key", PostgreSqlKeyConstraintKind.Unique,
                [new PostgreSqlKeyColumnMetadata("email", 1, false)])]);

        public Task EnsureDestinationAccessibleAsync(
            string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken) =>
            Access(true);

        private Task<IReadOnlyList<T>> Access<T>(IReadOnlyList<T> value) => ThrowAccess
            ? Task.FromException<IReadOnlyList<T>>(new PostgreSqlConnectionAccessException())
            : Task.FromResult(value);

        private Task Access(bool value) => ThrowAccess
            ? Task.FromException(new PostgreSqlConnectionAccessException())
            : Task.CompletedTask;
    }

    private sealed class SavedDiscovery(Discovery discovery) : ISavedConnectionMetadataDiscoveryService
    {
        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverPostgreSqlDatabasesAsync(Guid id, CancellationToken token) =>
            discovery.DiscoverDatabasesAsync(string.Empty, token);

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverPostgreSqlSchemasAsync(Guid id, string database, CancellationToken token) =>
            discovery.DiscoverSchemasAsync(string.Empty, database, token);

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverPostgreSqlTablesAsync(Guid id, string database, string schema, CancellationToken token) =>
            discovery.DiscoverTablesAsync(string.Empty, database, schema, token);

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverPostgreSqlColumnsAsync(Guid id, string database, string schema, string table, CancellationToken token) =>
            string.Equals(table, "removed", StringComparison.Ordinal)
                ? Task.FromException<IReadOnlyList<PostgreSqlColumnMetadata>>(new PostgreSqlMetadataObjectNotFoundException("table"))
                : discovery.DiscoverColumnsAsync(string.Empty, database, schema, table, token);

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverPostgreSqlKeyConstraintsAsync(Guid id, string database, string schema, string table, CancellationToken token) =>
            discovery.DiscoverKeyConstraintsAsync(string.Empty, database, schema, table, token);

        public Task EnsurePostgreSqlDestinationAccessibleAsync(Guid id, string database, string schema, string table, CancellationToken token) =>
            discovery.EnsureDestinationAccessibleAsync(string.Empty, database, schema, table, token);

        public Task<IReadOnlyList<MongoDatabaseMetadata>> DiscoverMongoDatabasesAsync(Guid id, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<MongoDatabaseMetadata>>([]);

        public Task<IReadOnlyList<MongoCollectionMetadata>> DiscoverMongoCollectionsAsync(Guid id, string database, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<MongoCollectionMetadata>>([]);

        public Task<IReadOnlyList<SourceFieldDefinition>> InferMongoSchemaAsync(Guid id, string database, string collection, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SourceFieldDefinition>>([]);
    }
}
