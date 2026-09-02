using System.Data.Common;
using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.Connections;
using EtlTool.Infrastructure.Sources;
using EtlTool.Application.Connections;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;

namespace EtlTool.UnitTests.Infrastructure.Sources;

public sealed class PreviewSourceFactoryTests
{
    [Theory]
    [InlineData(SourceType.Csv)]
    [InlineData(SourceType.Xlsx)]
    public async Task AcquireAsync_FileSourceDelegatesToWizardSourceStore(SourceType sourceType)
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(store);
        var pipeline = Pipeline(sourceType);

        var source = await factory.AcquireAsync(pipeline, CancellationToken.None);

        Assert.Same(store.Lease, source);
        Assert.Equal(1, store.AcquireCallCount);
        Assert.Equal(pipeline.Id, store.PipelineId);
        Assert.Equal(sourceType, store.SourceType);
        Assert.Same(pipeline.SourceOptions, store.SourceOptions);
    }

    [Fact]
    public async Task AcquireAsync_PostgreSqlMatchingLiveSchemaCreatesSourceWithoutFileAcquisition()
    {
        var store = new RecordingWizardSourceStore();
        var metadata = new RecordingPostgreSqlMetadataDiscoveryService();
        var factory = CreateFactory(store, postgreSqlMetadata: metadata);
        var pipeline = Pipeline(SourceType.PostgreSql);
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };
        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }
        ];
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true }
        ];

        await using var source = await factory.AcquireAsync(pipeline, CancellationToken.None);

        Assert.IsType<PostgreSqlEtlSource>(source);
        Assert.Equal(0, store.AcquireCallCount);
        Assert.Equal(1, metadata.DiscoverColumnsCallCount);
    }

    [Fact]
    public async Task AcquireAsync_SavedPostgreSqlSourceUsesResolvedRuntimeConnectionContext()
    {
        var connectionId = Guid.NewGuid();
        var fallbackMetadata = new RecordingPostgreSqlMetadataDiscoveryService();
        var savedMetadata = new RecordingPostgreSqlMetadataDiscoveryService();
        var resolver = new RecordingSavedConnectionRevisionResolver(connectionId, DatabaseProviderType.PostgreSql, 4);
        var contexts = new RecordingSavedConnectionRuntimeContextFactory
        {
            PostgreSqlContext = new PostgreSqlRuntimeConnectionContext(
                new ThrowingConnectionFactory(),
                savedMetadata)
        };
        var factory = CreateFactory(
            new RecordingWizardSourceStore(),
            postgreSqlMetadata: fallbackMetadata,
            savedConnectionRevisionResolver: resolver,
            savedConnectionRuntimeContextFactory: contexts);
        var pipeline = Pipeline(SourceType.PostgreSql);
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            SavedConnectionId = connectionId,
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };
        pipeline.ExpectedSchema = [Field("Id", SourceFieldType.Integer)];
        pipeline.FieldMappings = [new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true }];

        await using var source = await factory.AcquireAsync(pipeline, CancellationToken.None);

        Assert.IsType<PostgreSqlEtlSource>(source);
        Assert.Equal((connectionId, 4, DatabaseProviderType.PostgreSql), resolver.Request);
        Assert.Equal((connectionId, 4), contexts.PostgreSqlRequest);
        Assert.Equal(SavedConnectionProviderFactory.RuntimePostgreSqlProfile, savedMetadata.ConnectionProfile);
        Assert.Equal(0, fallbackMetadata.DiscoverColumnsCallCount);
        Assert.Equal(1, savedMetadata.DiscoverColumnsCallCount);
    }

    [Fact]
    public Task AcquireAsync_PostgreSqlAddedColumnRequiresRemappingBeforePreview() =>
        AssertPostgreSqlSchemaChangedAsync(
            [Field("Id", SourceFieldType.Integer)],
            [Column("Id", "integer", 1), Column("Name", "text", 2)]);

    [Fact]
    public Task AcquireAsync_PostgreSqlRemovedColumnRequiresRemappingBeforePreview() =>
        AssertPostgreSqlSchemaChangedAsync(
            [Field("Id", SourceFieldType.Integer), Field("Name", SourceFieldType.String)],
            [Column("Id", "integer", 1)]);

    [Fact]
    public Task AcquireAsync_PostgreSqlTypeChangeRequiresRemappingBeforePreview() =>
        AssertPostgreSqlSchemaChangedAsync(
            [Field("Id", SourceFieldType.Integer)],
            [Column("Id", "text", 1)]);

    [Fact]
    public Task AcquireAsync_PostgreSqlUnsupportedLiveTypeUsesSafeSchemaChangedFailure() =>
        AssertPostgreSqlSchemaChangedAsync(
            [Field("Id", SourceFieldType.Integer)],
            [Column("Id", "jsonb", 1)]);

    [Fact]
    public async Task AcquireAsync_MongoDbCreatesTheExistingMongoDbEtlSourceWithoutFileAcquisition()
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(store);
        var pipeline = Pipeline(SourceType.MongoDb);
        pipeline.MongoDbSource = new MongoDbSourceOptions
        {
            Database = "reporting",
            Collection = "customers"
        };
        pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }];

        await using var source = await factory.AcquireAsync(pipeline, CancellationToken.None);

        Assert.IsType<MongoDbEtlSource>(source);
        Assert.Equal(0, store.AcquireCallCount);
    }

    [Fact]
    public async Task AcquireAsync_SavedMongoDbSourceUsesResolvedRuntimeConnectionContext()
    {
        var connectionId = Guid.NewGuid();
        var fallbackInference = new RecordingMongoSchemaInferenceService();
        var savedInference = new RecordingMongoSchemaInferenceService();
        var resolver = new RecordingSavedConnectionRevisionResolver(connectionId, DatabaseProviderType.MongoDb, 7);
        var options = MongoOptions();
        var metadata = new MongoMetadataDatabase(options);
        var contexts = new RecordingSavedConnectionRuntimeContextFactory
        {
            MongoDbContext = new MongoRuntimeConnectionContext(
                metadata,
                new MongoTargetAccessService(metadata, options),
                savedInference,
                options)
        };
        var factory = CreateFactory(
            new RecordingWizardSourceStore(),
            mongoInference: fallbackInference,
            savedConnectionRevisionResolver: resolver,
            savedConnectionRuntimeContextFactory: contexts);
        var pipeline = Pipeline(SourceType.MongoDb);
        pipeline.MongoDbSource = new MongoDbSourceOptions
        {
            SavedConnectionId = connectionId,
            Database = "reporting",
            Collection = "customers"
        };
        pipeline.ExpectedSchema = [Field("Id", SourceFieldType.Integer)];

        await using var source = await factory.AcquireAsync(pipeline, CancellationToken.None);

        Assert.IsType<MongoDbEtlSource>(source);
        Assert.Equal((connectionId, 7, DatabaseProviderType.MongoDb), resolver.Request);
        Assert.Equal((connectionId, 7), contexts.MongoDbRequest);
        Assert.Equal(0, fallbackInference.CallCount);
        Assert.Equal(1, savedInference.CallCount);
    }

    [Fact]
    public async Task AcquireAsync_MongoDbSchemaDifferenceRequiresRemappingBeforePreview()
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(store);
        var pipeline = Pipeline(SourceType.MongoDb);
        pipeline.MongoDbSource = new MongoDbSourceOptions
        {
            Database = "reporting",
            Collection = "customers"
        };
        pipeline.ExpectedSchema = [new SourceFieldDefinition { Name = "Name", DataType = SourceFieldType.String }];
        pipeline.FieldMappings = [new FieldMapping { SourceField = "Name", TargetField = "name" }];

        var exception = await Assert.ThrowsAsync<MongoSourceSchemaChangedException>(() =>
            factory.AcquireAsync(pipeline, CancellationToken.None));

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
        Assert.Equal(0, store.AcquireCallCount);
    }

    [Fact]
    public async Task AcquireAsync_MongoDbIncompatibleLiveSchemaUsesSafeSchemaChangedFailure()
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(
            store,
            new RecordingMongoSchemaInferenceService
            {
                Failure = MongoSourceSchemaInferenceException.Unsupported("payload", "Array")
            });
        var pipeline = Pipeline(SourceType.MongoDb);
        pipeline.MongoDbSource = new MongoDbSourceOptions { Database = "reporting", Collection = "customers" };

        var exception = await Assert.ThrowsAsync<MongoSourceSchemaChangedException>(() =>
            factory.AcquireAsync(pipeline, CancellationToken.None));

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
        Assert.DoesNotContain("payload", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.AcquireCallCount);
    }

    [Fact]
    public async Task AcquireAsync_RejectsUnsupportedOrIncompleteSourcesAndPreservesCancellation()
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireAsync(Pipeline(SourceType.Unspecified), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireAsync(Pipeline(SourceType.PostgreSql), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireAsync(Pipeline(SourceType.MongoDb), CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.AcquireAsync(Pipeline(SourceType.Csv), cancellation.Token));
        Assert.Equal(0, store.AcquireCallCount);
    }

    private static PreviewSourceFactory CreateFactory(
        RecordingWizardSourceStore store,
        IMongoSourceSchemaInferenceService? mongoInference = null,
        IPostgreSqlMetadataDiscoveryService? postgreSqlMetadata = null,
        ISavedConnectionRevisionResolver? savedConnectionRevisionResolver = null,
        ISavedConnectionRuntimeContextFactory? savedConnectionRuntimeContextFactory = null) => new(
        store,
        new ThrowingConnectionFactory(),
        postgreSqlMetadata ?? new RecordingPostgreSqlMetadataDiscoveryService(),
        new PostgreSqlSourceSchemaConverter(),
        new PostgreSqlDeterministicOrderingResolver(),
        new MongoMetadataDatabase(MongoOptions()),
        MongoOptions(),
        mongoInference ?? new RecordingMongoSchemaInferenceService(),
        new SourceSchemaComparisonService(),
        savedConnectionRevisionResolver,
        savedConnectionRuntimeContextFactory);

    private static async Task AssertPostgreSqlSchemaChangedAsync(
        IReadOnlyList<SourceFieldDefinition> expectedSchema,
        IReadOnlyList<PostgreSqlColumnMetadata> liveColumns)
    {
        var store = new RecordingWizardSourceStore();
        var metadata = new RecordingPostgreSqlMetadataDiscoveryService { Columns = liveColumns };
        var factory = CreateFactory(store, postgreSqlMetadata: metadata);
        var pipeline = Pipeline(SourceType.PostgreSql);
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };
        pipeline.ExpectedSchema = expectedSchema.ToList();
        pipeline.FieldMappings = expectedSchema
            .Select(field => new FieldMapping
            {
                SourceField = field.Name,
                TargetField = field.Name.ToLowerInvariant(),
                IsIncluded = true
            })
            .ToList();

        var exception = await Assert.ThrowsAsync<PostgreSqlSourceSchemaChangedException>(() =>
            factory.AcquireAsync(pipeline, CancellationToken.None));

        Assert.Equal(PostgreSqlSourceSchemaChangedException.SafeMessage, exception.Message);
        Assert.Equal(1, metadata.DiscoverColumnsCallCount);
        Assert.Equal(0, store.AcquireCallCount);
    }

    private static SourceFieldDefinition Field(string name, SourceFieldType dataType) => new()
    {
        Name = name,
        DataType = dataType
    };

    private static PostgreSqlColumnMetadata Column(string name, string nativeType, int ordinal) =>
        new(name, nativeType, true, ordinal);

    private static MongoDbOptions MongoOptions() => new()
    {
        ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
        MetadataDatabaseName = "etl_tool_preview_source_factory_tests"
    };

    private static PipelineDefinition Pipeline(SourceType sourceType) => new()
    {
        Id = Guid.NewGuid(),
        SourceType = sourceType,
        SourceOptions = new SourceOptions { FirstRowIsHeader = true }
    };

    private sealed class RecordingWizardSourceStore : IWizardSourceStore
    {
        public IWizardSourceLease Lease { get; } = new EmptyLease();
        public int AcquireCallCount { get; private set; }
        public Guid PipelineId { get; private set; }
        public SourceType SourceType { get; private set; }
        public SourceOptions? SourceOptions { get; private set; }

        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
            return Task.FromResult<IWizardSourceLease?>(Lease);
        }

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyLease : IWizardSourceLease
    {
        public async IAsyncEnumerable<DataRow> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingConnectionFactory : IPostgreSqlConnectionFactory
    {
        public Task<DbConnection> OpenAsync(string connectionProfile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DbConnection> OpenDatabaseAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingPostgreSqlMetadataDiscoveryService : IPostgreSqlRuntimeMetadataDiscoveryService
    {
        public IReadOnlyList<PostgreSqlColumnMetadata> Columns { get; init; } =
            [new PostgreSqlColumnMetadata("Id", "integer", false, 1)];

        public int DiscoverColumnsCallCount { get; private set; }

        public string? ConnectionProfile { get; private set; }

        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(string connectionProfile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(string connectionProfile, string database, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(string connectionProfile, string database, string schema, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken)
        {
            DiscoverColumnsCallCount++;
            ConnectionProfile = connectionProfile;
            return Task.FromResult(Columns);
        }

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnsureDestinationAccessibleAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingMongoSchemaInferenceService : IMongoSourceSchemaInferenceService
    {
        public Exception? Failure { get; init; }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SourceFieldDefinition>> InferAsync(
            MongoDbSourceOptions source,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Failure is null
                ? Task.FromResult<IReadOnlyList<SourceFieldDefinition>>
                    ([new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }])
                : Task.FromException<IReadOnlyList<SourceFieldDefinition>>(Failure);
        }
    }

    private sealed class RecordingSavedConnectionRevisionResolver(
        Guid connectionId,
        DatabaseProviderType providerType,
        int revision) : ISavedConnectionRevisionResolver
    {
        public (Guid ConnectionId, int Revision, DatabaseProviderType ProviderType)? Request { get; private set; }

        public Task<SavedConnectionReference> ResolveCurrentAsync(
            Guid requestedConnectionId,
            DatabaseProviderType requestedProviderType,
            CancellationToken cancellationToken)
        {
            Request = (requestedConnectionId, revision, requestedProviderType);
            return Task.FromResult(new SavedConnectionReference
            {
                ConnectionId = connectionId,
                ProviderType = providerType,
                Revision = revision
            });
        }
    }

    private sealed class RecordingSavedConnectionRuntimeContextFactory : ISavedConnectionRuntimeContextFactory
    {
        public PostgreSqlRuntimeConnectionContext? PostgreSqlContext { get; init; }

        public MongoRuntimeConnectionContext? MongoDbContext { get; init; }

        public (Guid ConnectionId, int Revision)? PostgreSqlRequest { get; private set; }

        public (Guid ConnectionId, int Revision)? MongoDbRequest { get; private set; }

        public Task<PostgreSqlRuntimeConnectionContext> CreatePostgreSqlAsync(
            Guid connectionId,
            int revision,
            CancellationToken cancellationToken)
        {
            PostgreSqlRequest = (connectionId, revision);
            return Task.FromResult(PostgreSqlContext ?? throw new InvalidOperationException());
        }

        public Task<MongoRuntimeConnectionContext> CreateMongoDbAsync(
            Guid connectionId,
            int revision,
            CancellationToken cancellationToken)
        {
            MongoDbRequest = (connectionId, revision);
            return Task.FromResult(MongoDbContext ?? throw new InvalidOperationException());
        }
    }
}
