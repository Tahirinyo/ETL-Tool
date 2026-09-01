using System.Data.Common;
using EtlTool.Application.Extraction;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Execution;
using EtlTool.Infrastructure.Extraction;
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
        IPostgreSqlMetadataDiscoveryService? metadataDiscoveryService = null)
    {
        var uploadOptions = new UploadStorageOptions { RootPath = _rootPath };
        var fileStore = new LocalRunSourceFileStore(
            uploadOptions,
            new LocalUploadStorage(uploadOptions),
            new FileExtractorResolver([new CsvFileExtractor(), new XlsxFileExtractor()]));
        return new RunSourceStore(
            fileStore,
            connectionFactory ?? new RecordingConnectionFactory(),
            metadataDiscoveryService ?? new RecordingMetadataDiscoveryService(),
            new PostgreSqlSourceSchemaConverter(),
            new SourceSchemaComparisonService(),
            new PostgreSqlDeterministicOrderingResolver());
    }

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

    private sealed class RecordingMetadataDiscoveryService : IPostgreSqlMetadataDiscoveryService
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
    }

    private sealed class TestConnectionException : Exception
    {
        public TestConnectionException()
            : base("The test connection factory was reached.")
        {
        }
    }
}
