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
using EtlTool.Infrastructure.Sources;
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
        IPostgreSqlMetadataDiscoveryService? postgreSqlMetadata = null) => new(
        store,
        new ThrowingConnectionFactory(),
        postgreSqlMetadata ?? new RecordingPostgreSqlMetadataDiscoveryService(),
        new PostgreSqlSourceSchemaConverter(),
        new PostgreSqlDeterministicOrderingResolver(),
        new MongoMetadataDatabase(MongoOptions()),
        MongoOptions(),
        mongoInference ?? new RecordingMongoSchemaInferenceService(),
        new SourceSchemaComparisonService());

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

    private sealed class RecordingPostgreSqlMetadataDiscoveryService : IPostgreSqlMetadataDiscoveryService
    {
        public IReadOnlyList<PostgreSqlColumnMetadata> Columns { get; init; } =
            [new PostgreSqlColumnMetadata("Id", "integer", false, 1)];

        public int DiscoverColumnsCallCount { get; private set; }

        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(string connectionProfile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(string connectionProfile, string database, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(string connectionProfile, string database, string schema, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken)
        {
            DiscoverColumnsCallCount++;
            return Task.FromResult(Columns);
        }

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingMongoSchemaInferenceService : IMongoSourceSchemaInferenceService
    {
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<SourceFieldDefinition>> InferAsync(
            MongoDbSourceOptions source,
            CancellationToken cancellationToken) => Failure is null
                ? Task.FromResult<IReadOnlyList<SourceFieldDefinition>>
                    ([new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }])
                : Task.FromException<IReadOnlyList<SourceFieldDefinition>>(Failure);
    }
}
