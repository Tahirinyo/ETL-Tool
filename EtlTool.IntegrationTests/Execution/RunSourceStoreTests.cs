using System.Data.Common;
using EtlTool.Application.Extraction;
using EtlTool.Application.Connections;
using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Execution;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.Connections;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.Uploads;

namespace EtlTool.IntegrationTests.Execution;

public sealed class RunSourceStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        $"EtlTool-RunSourceFactory-{Guid.NewGuid():N}");

    [Fact]
    public async Task OpenAndReleaseAsync_FileRunRetainsExistingFileLifecycle()
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, $"{Guid.NewGuid():N}.upload");
        await File.WriteAllTextAsync(path, "Id\nsource");
        var store = CreateStore();
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            StoredFilePath = path,
            ExecutionConfiguration = new EtlRunExecutionConfiguration
            {
                SourceType = SourceType.Csv,
                SourceOptions = new SourceOptions
                {
                    Delimiter = CsvDelimiter.Comma,
                    FirstRowIsHeader = true
                }
            }
        };

        await using (var source = await store.OpenAsync(run, CancellationToken.None))
        {
            var rows = new List<DataRow>();
            await foreach (var row in source.ReadAsync(CancellationToken.None))
            {
                rows.Add(row);
            }

            Assert.Equal("source", Assert.Single(rows).Values["Id"]);
        }

        await store.ReleaseAsync(run, CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OpenAndReleaseAsync_PostgreSqlRunUsesCapturedLogicalIdentityWithoutFileArtifact()
    {
        var metadata = new RecordingMetadataDiscoveryService();
        var connectionFactory = new RecordingConnectionFactory();
        var store = CreateStore(connectionFactory, metadata);
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            StoredFilePath = string.Empty,
            ExecutionConfiguration = new EtlRunExecutionConfiguration
            {
                SourceType = SourceType.PostgreSql,
                ExpectedSchema = [Field("Id", SourceFieldType.Integer)],
                FieldMappings = [Mapping("Id", "id")],
                PostgreSqlSource = new PostgreSqlSourceOptions
                {
                    ConnectionProfile = "ReportingDb",
                    Database = "reporting",
                    Schema = "sales",
                    Table = "customers"
                }
            }
        };

        await using var source = await store.OpenAsync(run, CancellationToken.None);
        Assert.IsType<PostgreSqlEtlSource>(source);

        var failure = await Assert.ThrowsAsync<TestConnectionException>(async () =>
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None))
            {
            }
        });

        Assert.Equal("The test connection factory was reached.", failure.Message);
        Assert.Equal(("ReportingDb", "reporting", "sales", "customers"), metadata.LastRequest);
        Assert.Equal(("ReportingDb", "reporting", "sales", "customers"), metadata.LastColumnRequest);
        Assert.Equal(("ReportingDb", "reporting"), connectionFactory.LastRequest);

        await store.ReleaseAsync(run, CancellationToken.None);
    }

    [Fact]
    public async Task OpenAsync_SavedPostgreSqlRunUsesFrozenReferenceAndLogicalIdentity()
    {
        var connectionId = Guid.NewGuid();
        var runtimeMetadata = new RecordingMetadataDiscoveryService();
        var runtimeConnectionFactory = new RecordingConnectionFactory();
        var runtimeFactory = new RecordingSavedRuntimeContextFactory
        {
            PostgreSqlContext = new PostgreSqlRuntimeConnectionContext(
                runtimeConnectionFactory,
                runtimeMetadata)
        };
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.PostgreSql,
            ExpectedSchema = [Field("Id", SourceFieldType.Integer)],
            FieldMappings = [Mapping("Id", "id")],
            PostgreSqlSource = new PostgreSqlSourceOptions
            {
                SavedConnectionId = connectionId,
                ConnectionProfile = "must-not-be-used",
                Database = "etl_demo",
                Schema = "public",
                Table = "customers_csv_clean"
            }
        };
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(
                pipeline,
                SavedReference(connectionId, DatabaseProviderType.PostgreSql, revision: 6))
        };

        await using var source = await CreateStore(
                savedConnectionRuntimeContextFactory: runtimeFactory)
            .OpenAsync(run, CancellationToken.None);

        Assert.IsType<PostgreSqlEtlSource>(source);
        Assert.Equal(
            (connectionId, DatabaseProviderType.PostgreSql, 6),
            Assert.Single(runtimeFactory.PostgreSqlRequests));
        Assert.Equal(
            (SavedConnectionProviderFactory.RuntimePostgreSqlProfile, "etl_demo", "public", "customers_csv_clean"),
            runtimeMetadata.LastColumnRequest);
        Assert.Null(run.ExecutionConfiguration!.PostgreSqlSource!.SavedConnectionRevision);
        var serialized = System.Text.Json.JsonSerializer.Serialize(run.ExecutionConfiguration);
        Assert.DoesNotContain("ConnectionString", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_SavedMongoDbRunUsesFrozenReferenceAndLogicalIdentity()
    {
        var connectionId = Guid.NewGuid();
        var runtimeOptions = MongoOptions();
        var runtimeMetadata = new MongoMetadataDatabase(runtimeOptions);
        var runtimeInference = new RecordingMongoSchemaInferenceService();
        var runtimeFactory = new RecordingSavedRuntimeContextFactory
        {
            MongoDbContext = new MongoRuntimeConnectionContext(
                runtimeMetadata,
                new MongoTargetAccessService(runtimeMetadata, runtimeOptions),
                runtimeInference,
                runtimeOptions)
        };
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.MongoDb,
            ExpectedSchema = [Field("Id", SourceFieldType.Integer)],
            FieldMappings = [Mapping("Id", "id")],
            MongoDbSource = new MongoDbSourceOptions
            {
                SavedConnectionId = connectionId,
                Database = "etl_demo",
                Collection = "customers_csv_clean"
            }
        };
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(
                pipeline,
                SavedReference(connectionId, DatabaseProviderType.MongoDb, revision: 9))
        };

        await using var source = await CreateStore(
                savedConnectionRuntimeContextFactory: runtimeFactory)
            .OpenAsync(run, CancellationToken.None);

        Assert.IsType<MongoDbEtlSource>(source);
        Assert.Equal(
            (connectionId, DatabaseProviderType.MongoDb, 9),
            Assert.Single(runtimeFactory.MongoDbRequests));
        Assert.Equal(("etl_demo", "customers_csv_clean"), runtimeInference.LastRequest);
        Assert.Null(run.ExecutionConfiguration!.MongoDbSource!.SavedConnectionRevision);
    }

    [Theory]
    [InlineData(SourceType.PostgreSql)]
    [InlineData(SourceType.MongoDb)]
    public async Task OpenAsync_SavedSourceUnavailableAtExecutionFailsWithoutLegacyFallback(
        SourceType sourceType)
    {
        var connectionId = Guid.NewGuid();
        var pipeline = sourceType == SourceType.PostgreSql
            ? new PipelineDefinition
            {
                SourceType = sourceType,
                ExpectedSchema = [Field("Id", SourceFieldType.Integer)],
                FieldMappings = [Mapping("Id", "id")],
                PostgreSqlSource = new PostgreSqlSourceOptions
                {
                    SavedConnectionId = connectionId,
                    ConnectionProfile = "must-not-be-used",
                    Database = "etl_demo",
                    Schema = "public",
                    Table = "customers_csv_clean"
                }
            }
            : new PipelineDefinition
            {
                SourceType = sourceType,
                ExpectedSchema = [Field("Id", SourceFieldType.Integer)],
                FieldMappings = [Mapping("Id", "id")],
                MongoDbSource = new MongoDbSourceOptions
                {
                    SavedConnectionId = connectionId,
                    Database = "etl_demo",
                    Collection = "customers_csv_clean"
                }
            };
        var providerType = sourceType == SourceType.PostgreSql
            ? DatabaseProviderType.PostgreSql
            : DatabaseProviderType.MongoDb;
        var runtimeFactory = new RecordingSavedRuntimeContextFactory
        {
            Failure = new SavedConnectionResolutionException()
        };
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(
                pipeline,
                SavedReference(connectionId, providerType, revision: 2))
        };

        await Assert.ThrowsAsync<SavedConnectionResolutionException>(() =>
            CreateStore(savedConnectionRuntimeContextFactory: runtimeFactory)
                .OpenAsync(run, CancellationToken.None));

        Assert.Equal(1, runtimeFactory.RequestCount);
    }

    [Fact]
    public async Task OpenAsync_PostgreSqlTypeChangeRequiresRemappingBeforeSourceEnumeration()
    {
        var metadata = new RecordingMetadataDiscoveryService
        {
            Columns = [new PostgreSqlColumnMetadata("Id", "text", false, 1)]
        };
        var connectionFactory = new RecordingConnectionFactory();
        var store = CreateStore(connectionFactory, metadata);
        var run = PostgreSqlRun([Field("Id", SourceFieldType.Integer)]);

        var exception = await Assert.ThrowsAsync<PostgreSqlSourceSchemaChangedException>(() =>
            store.OpenAsync(run, CancellationToken.None));

        Assert.Equal(PostgreSqlSourceSchemaChangedException.SafeMessage, exception.Message);
        Assert.Null(metadata.LastRequest);
        Assert.Null(connectionFactory.LastRequest);
    }

    [Fact]
    public async Task OpenAsync_PostgreSqlRemovedMappedColumnRequiresRemappingBeforeSourceEnumeration()
    {
        var metadata = new RecordingMetadataDiscoveryService
        {
            Columns = [new PostgreSqlColumnMetadata("Id", "integer", false, 1)]
        };
        var connectionFactory = new RecordingConnectionFactory();
        var store = CreateStore(connectionFactory, metadata);
        var run = PostgreSqlRun(
            [Field("Id", SourceFieldType.Integer), Field("Name", SourceFieldType.String)]);

        await Assert.ThrowsAsync<PostgreSqlSourceSchemaChangedException>(() =>
            store.OpenAsync(run, CancellationToken.None));

        Assert.Null(metadata.LastRequest);
        Assert.Null(connectionFactory.LastRequest);
    }

    [Fact]
    public async Task OpenAsync_PostgreSqlAddedColumnRequiresRemappingBeforeSourceEnumeration()
    {
        var metadata = new RecordingMetadataDiscoveryService
        {
            Columns =
            [
                new PostgreSqlColumnMetadata("Id", "integer", false, 1),
                new PostgreSqlColumnMetadata("Name", "text", true, 2)
            ]
        };
        var connectionFactory = new RecordingConnectionFactory();
        var store = CreateStore(connectionFactory, metadata);
        var run = PostgreSqlRun([Field("Id", SourceFieldType.Integer)]);

        await Assert.ThrowsAsync<PostgreSqlSourceSchemaChangedException>(() =>
            store.OpenAsync(run, CancellationToken.None));

        Assert.Null(metadata.LastRequest);
        Assert.Null(connectionFactory.LastRequest);
    }

    [Fact]
    public async Task OpenAsync_PostgreSqlUsesCapturedExpectedSchemaAfterPipelineChanges()
    {
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.PostgreSql,
            ExpectedSchema = [Field("Id", SourceFieldType.Integer)],
            FieldMappings = [Mapping("Id", "id")],
            PostgreSqlSource = PostgreSqlOptions()
        };
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(pipeline)
        };
        pipeline.ExpectedSchema[0].DataType = SourceFieldType.String;
        var metadata = new RecordingMetadataDiscoveryService();

        await using var source = await CreateStore(
            new RecordingConnectionFactory(),
            metadata).OpenAsync(run, CancellationToken.None);

        Assert.IsType<PostgreSqlEtlSource>(source);
        Assert.Equal(SourceFieldType.Integer, run.ExecutionConfiguration.ExpectedSchema[0].DataType);
        Assert.Equal(("ReportingDb", "reporting", "sales", "customers"), metadata.LastColumnRequest);
    }

    [Fact]
    public async Task OpenAsync_MongoDbUsesCapturedIdentityAndExpectedSchemaAfterPipelineChanges()
    {
        var pipeline = new PipelineDefinition
        {
            SourceType = SourceType.MongoDb,
            ExpectedSchema = [Field("Id", SourceFieldType.Integer)],
            FieldMappings = [Mapping("Id", "id")],
            MongoDbSource = new MongoDbSourceOptions
            {
                Database = "reporting",
                Collection = "customers"
            }
        };
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            ExecutionConfiguration = EtlRunExecutionConfiguration.Capture(pipeline)
        };
        pipeline.MongoDbSource.Collection = "edited_after_admission";
        pipeline.ExpectedSchema[0].DataType = SourceFieldType.String;
        var inference = new RecordingMongoSchemaInferenceService
        {
            Schema = [Field("Id", SourceFieldType.Integer)]
        };

        await using var source = await CreateStore(mongoInferenceService: inference)
            .OpenAsync(run, CancellationToken.None);

        Assert.IsType<MongoDbEtlSource>(source);
        Assert.Equal(("reporting", "customers"), inference.LastRequest);
        Assert.Equal(SourceFieldType.Integer, run.ExecutionConfiguration.ExpectedSchema[0].DataType);
    }

    [Fact]
    public async Task OpenAsync_MongoDbSchemaDifferenceRequiresRemappingBeforeEnumeration()
    {
        var run = MongoDbRun([Field("Id", SourceFieldType.Integer)]);
        var inference = new RecordingMongoSchemaInferenceService
        {
            Schema = [Field("Id", SourceFieldType.String)]
        };

        var exception = await Assert.ThrowsAsync<MongoSourceSchemaChangedException>(() =>
            CreateStore(mongoInferenceService: inference)
                .OpenAsync(run, CancellationToken.None));

        Assert.Equal(MongoSourceSchemaChangedException.SafeMessage, exception.Message);
        Assert.Equal(("reporting", "customers"), inference.LastRequest);
    }

    [Fact]
    public async Task ReleaseAsync_MongoDbRunDoesNotDeletePhysicalPath()
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, $"{Guid.NewGuid():N}.sentinel");
        await File.WriteAllTextAsync(path, "logical source");
        var run = MongoDbRun([Field("Id", SourceFieldType.Integer)]);
        run.StoredFilePath = path;

        await CreateStore().ReleaseAsync(run, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ReleaseAsync_LegacyRunWithoutSnapshotRetainsFileCleanupBehavior()
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, $"{Guid.NewGuid():N}.upload");
        await File.WriteAllTextAsync(path, "legacy");
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            StoredFilePath = path,
            ExecutionConfiguration = null
        };

        await CreateStore().ReleaseAsync(run, CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private RunSourceStore CreateStore(
        IPostgreSqlConnectionFactory? connectionFactory = null,
        IPostgreSqlMetadataDiscoveryService? metadataDiscoveryService = null,
        IMongoSourceSchemaInferenceService? mongoInferenceService = null,
        ISavedConnectionRuntimeContextFactory? savedConnectionRuntimeContextFactory = null)
    {
        var uploadOptions = new UploadStorageOptions { RootPath = _rootPath };
        var fileStore = new LocalRunSourceFileStore(
            uploadOptions,
            new LocalUploadStorage(uploadOptions),
            new FileExtractorResolver([new CsvFileExtractor(), new XlsxFileExtractor()]));
        var mongoOptions = MongoOptions();
        var mongoMetadata = new MongoMetadataDatabase(mongoOptions);
        var mongoTargetAccess = new MongoTargetAccessService(mongoMetadata, mongoOptions);
        var mongoDiscovery = new MongoSourceMetadataDiscoveryService(
            mongoMetadata,
            mongoTargetAccess);
        return new RunSourceStore(
            fileStore,
            connectionFactory ?? new RecordingConnectionFactory(),
            metadataDiscoveryService ?? new RecordingMetadataDiscoveryService(),
            new PostgreSqlSourceSchemaConverter(),
            new SourceSchemaComparisonService(),
            new PostgreSqlDeterministicOrderingResolver(),
            mongoMetadata,
            mongoInferenceService ?? new MongoSourceSchemaInferenceService(
                mongoMetadata,
                mongoDiscovery,
                mongoOptions),
            mongoOptions,
            savedConnectionRuntimeContextFactory);
    }

    private static MongoDbOptions MongoOptions() => new()
    {
        ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
        MetadataDatabaseName = "etl_tool_run_source_store_tests"
    };

    private static EtlRun PostgreSqlRun(IReadOnlyList<SourceFieldDefinition> expectedSchema) => new()
    {
        Id = Guid.NewGuid(),
        ExecutionConfiguration = new EtlRunExecutionConfiguration
        {
            SourceType = SourceType.PostgreSql,
            ExpectedSchema = expectedSchema.ToList(),
            FieldMappings = expectedSchema.Select(field => Mapping(field.Name, field.Name)).ToList(),
            PostgreSqlSource = PostgreSqlOptions()
        }
    };

    private static EtlRun MongoDbRun(IReadOnlyList<SourceFieldDefinition> expectedSchema) => new()
    {
        Id = Guid.NewGuid(),
        ExecutionConfiguration = new EtlRunExecutionConfiguration
        {
            SourceType = SourceType.MongoDb,
            ExpectedSchema = expectedSchema.ToList(),
            FieldMappings = expectedSchema.Select(field => Mapping(field.Name, field.Name)).ToList(),
            MongoDbSource = new MongoDbSourceOptions
            {
                Database = "reporting",
                Collection = "customers"
            }
        }
    };

    private static PostgreSqlSourceOptions PostgreSqlOptions() => new()
    {
        ConnectionProfile = "ReportingDb",
        Database = "reporting",
        Schema = "sales",
        Table = "customers"
    };

    private static SourceFieldDefinition Field(string name, SourceFieldType dataType) => new()
    {
        Name = name,
        DataType = dataType
    };

    private static FieldMapping Mapping(string sourceField, string targetField) => new()
    {
        SourceField = sourceField,
        TargetField = targetField,
        IsIncluded = true
    };

    private static SavedConnectionReference SavedReference(
        Guid connectionId,
        DatabaseProviderType providerType,
        int revision) => new()
    {
        ConnectionId = connectionId,
        ProviderType = providerType,
        Revision = revision
    };

    private sealed class RecordingConnectionFactory : IPostgreSqlConnectionFactory
    {
        public (string Profile, string Database)? LastRequest { get; private set; }

        public Task<DbConnection> OpenAsync(
            string connectionProfile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DbConnection> OpenDatabaseAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken)
        {
            LastRequest = (connectionProfile, database);
            throw new TestConnectionException();
        }
    }

    private sealed class RecordingMetadataDiscoveryService : IPostgreSqlRuntimeMetadataDiscoveryService
    {
        public (string Profile, string Database, string Schema, string Table)? LastRequest { get; private set; }

        public (string Profile, string Database, string Schema, string Table)? LastColumnRequest { get; private set; }

        public IReadOnlyList<PostgreSqlColumnMetadata> Columns { get; init; } =
            [new PostgreSqlColumnMetadata("Id", "integer", false, 1)];

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            LastRequest = (connectionProfile, database, schema, table);
            return Task.FromResult<IReadOnlyList<PostgreSqlKeyConstraintMetadata>>
            ([
                new PostgreSqlKeyConstraintMetadata(
                    "customers_pkey",
                    PostgreSqlKeyConstraintKind.PrimaryKey,
                    [new PostgreSqlKeyColumnMetadata("Id", 1, IsNullable: false)])
            ]);
        }

        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(
            string connectionProfile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(
            string connectionProfile,
            string database,
            string schema,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken)
        {
            LastColumnRequest = (connectionProfile, database, schema, table);
            return Task.FromResult(Columns);
        }

        public Task EnsureDestinationAccessibleAsync(
            string connectionProfile,
            string database,
            string schema,
            string table,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestConnectionException : Exception
    {
        public TestConnectionException()
            : base("The test connection factory was reached.")
        {
        }
    }

    private sealed class RecordingMongoSchemaInferenceService : IMongoSourceSchemaInferenceService
    {
        public (string Database, string Collection)? LastRequest { get; private set; }

        public IReadOnlyList<SourceFieldDefinition> Schema { get; init; } =
            [Field("Id", SourceFieldType.Integer)];

        public Task<IReadOnlyList<SourceFieldDefinition>> InferAsync(
            MongoDbSourceOptions source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = (source.Database, source.Collection);
            return Task.FromResult(Schema);
        }
    }

    private sealed class RecordingSavedRuntimeContextFactory : ISavedConnectionRuntimeContextFactory
    {
        public PostgreSqlRuntimeConnectionContext? PostgreSqlContext { get; init; }

        public MongoRuntimeConnectionContext? MongoDbContext { get; init; }

        public Exception? Failure { get; init; }

        public List<(Guid ConnectionId, DatabaseProviderType ProviderType, int Revision)> PostgreSqlRequests { get; } = [];

        public List<(Guid ConnectionId, DatabaseProviderType ProviderType, int Revision)> MongoDbRequests { get; } = [];

        public int RequestCount => PostgreSqlRequests.Count + MongoDbRequests.Count;

        public Task<PostgreSqlRuntimeConnectionContext> CreatePostgreSqlAsync(
            Guid connectionId,
            int revision,
            CancellationToken cancellationToken)
        {
            PostgreSqlRequests.Add((connectionId, DatabaseProviderType.PostgreSql, revision));
            return Failure is null
                ? Task.FromResult(PostgreSqlContext
                    ?? throw new InvalidOperationException("PostgreSQL runtime context was not configured."))
                : Task.FromException<PostgreSqlRuntimeConnectionContext>(Failure);
        }

        public Task<MongoRuntimeConnectionContext> CreateMongoDbAsync(
            Guid connectionId,
            int revision,
            CancellationToken cancellationToken)
        {
            MongoDbRequests.Add((connectionId, DatabaseProviderType.MongoDb, revision));
            return Failure is null
                ? Task.FromResult(MongoDbContext
                    ?? throw new InvalidOperationException("MongoDB runtime context was not configured."))
                : Task.FromException<MongoRuntimeConnectionContext>(Failure);
        }
    }
}
