using System.Data.Common;
using EtlTool.Application.Mapping;
using EtlTool.Application.Processing;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Application.Sources;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.Sources;
using EtlTool.Application.PostgreSql;
using EtlDataRow = EtlTool.Application.Extraction.DataRow;

namespace EtlTool.IntegrationTests.PostgreSql;

[Collection(PostgreSqlTestCollection.CollectionName)]
public sealed class PostgreSqlEtlSourceIntegrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task ReadAsync_StreamsQuotedTableRowsWithProviderValuesAndNulls()
    {
        const string schema = "DB8 \"Mixed Schema";
        const string table = "Rows \"Table";
        var factory = CreateFactory();
        var options = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = GetDatabaseName(),
            Schema = schema,
            Table = table
        };

        await using var setupConnection = await factory.OpenAsync(
            "ReportingDb",
            CancellationToken.None);
        try
        {
            await ExecuteAsync(
                setupConnection,
                "CREATE SCHEMA \"DB8 \"\"Mixed Schema\"; " +
                "CREATE TABLE \"DB8 \"\"Mixed Schema\".\"Rows \"\"Table\" " +
                "(\"Id Number\" integer PRIMARY KEY, \"Display Name\" text, \"Amount\" numeric, \"Is Active\" boolean); " +
                "INSERT INTO \"DB8 \"\"Mixed Schema\".\"Rows \"\"Table\" VALUES " +
                "(3, 'Linus', 7.25, TRUE), (1, ' Ada ', 12.50, TRUE), (2, NULL, NULL, FALSE);");

            await using var source = new PostgreSqlEtlSource(
                factory,
                new PostgreSqlMetadataDiscoveryService(factory),
                new PostgreSqlDeterministicOrderingResolver(),
                options);
            options.Schema = "Changed";
            options.Table = "Changed";

            var rows = await ReadAllAsync(source.ReadAsync(CancellationToken.None));
            var repeatedRows = await ReadAllAsync(source.ReadAsync(CancellationToken.None));

            Assert.Equal(3, rows.Count);
            Assert.Equal([1L, 2L, 3L], rows.Select(row => row.SourceRowNumber));
            Assert.Equal([1, 2, 3], rows.Select(row => Assert.IsType<int>(row.Values["Id Number"])));
            Assert.Equal(
                rows.Select(row => row.Values["Id Number"]),
                repeatedRows.Select(row => row.Values["Id Number"]));
            var byId = rows.ToDictionary(row => Assert.IsType<int>(row.Values["Id Number"]));
            Assert.Equal(" Ada ", byId[1].Values["Display Name"]);
            Assert.Equal(12.50m, byId[1].Values["Amount"]);
            Assert.True(Assert.IsType<bool>(byId[1].Values["Is Active"]));
            Assert.Null(byId[2].Values["Display Name"]);
            Assert.Null(byId[2].Values["Amount"]);
            Assert.False(Assert.IsType<bool>(byId[2].Values["Is Active"]));

            var mapping = new FieldMappingService().Prepare(new PipelineDefinition
            {
                ExpectedSchema =
                [
                    new SourceFieldDefinition { Name = "Id Number" },
                    new SourceFieldDefinition { Name = "Display Name" }
                ],
                FieldMappings =
                [
                    new FieldMapping
                    {
                        SourceField = "Id Number",
                        TargetField = "id",
                        IsIncluded = true
                    },
                    new FieldMapping
                    {
                        SourceField = "Display Name",
                        TargetField = "name",
                        IsIncluded = true
                    }
                ]
            });
            var mapped = new FieldMappingService().Apply(byId[1], mapping);
            var transformed = new TrimTransformationHandler().Apply(
                mapped,
                new TransformationRule
                {
                    Type = TransformationType.Trim,
                    SourceField = "name"
                });
            var validation = new RequiredValidationHandler().Validate(
                transformed.Row,
                new ValidationRule
                {
                    Type = ValidationType.Required,
                    Field = "name"
                });

            Assert.Equal(1, mapped.Values["id"]);
            Assert.Equal("Ada", mapped.Values["name"]);
            Assert.True(validation.IsValid);
        }
        finally
        {
            await ExecuteAsync(
                setupConnection,
                "DROP SCHEMA IF EXISTS \"DB8 \"\"Mixed Schema\" CASCADE;");
        }
    }

    [Fact]
    public async Task ReadAsync_UsesEligibleUniqueConstraintAndRejectsNoKeyTable()
    {
        const string schema = "DB9 Ordering";
        var factory = CreateFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(
                setupConnection,
                "CREATE SCHEMA \"DB9 Ordering\"; " +
                "CREATE TABLE \"DB9 Ordering\".\"Unique Rows\" " +
                "(\"Code\" integer NOT NULL UNIQUE, \"Value\" text); " +
                "INSERT INTO \"DB9 Ordering\".\"Unique Rows\" VALUES (30, 'third'), (10, 'first'), (20, 'second'); " +
                "CREATE TABLE \"DB9 Ordering\".\"No Key Rows\" (\"Value\" text); " +
                "CREATE TABLE \"DB9 Ordering\".\"Nullable Unique Rows\" (\"Email\" text UNIQUE); " +
                "CREATE TABLE \"DB9 Ordering\".\"Nulls Not Distinct Rows\" (\"Email\" text NOT NULL UNIQUE NULLS NOT DISTINCT); " +
                "CREATE TABLE \"DB9 Ordering\".\"Composite Rows\" " +
                "(\"First\" integer NOT NULL, \"Second\" integer NOT NULL, PRIMARY KEY (\"First\", \"Second\")); " +
                "INSERT INTO \"DB9 Ordering\".\"Composite Rows\" VALUES (2, 1), (1, 2), (1, 1); " +
                "CREATE TABLE \"DB9 Ordering\".\"First Valid Rows\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Logical Id\" text NOT NULL); " +
                "INSERT INTO \"DB9 Ordering\".\"First Valid Rows\" VALUES (2, 'A'), (1, 'A');");

            await using var uniqueSource = CreateSource(factory, schema, "Unique Rows");
            var uniqueRows = await ReadAllAsync(uniqueSource.ReadAsync(CancellationToken.None));
            var repeatedUniqueRows = await ReadAllAsync(uniqueSource.ReadAsync(CancellationToken.None));

            Assert.Equal([10, 20, 30], uniqueRows.Select(row => Assert.IsType<int>(row.Values["Code"])));
            Assert.Equal(
                uniqueRows.Select(row => row.Values["Code"]),
                repeatedUniqueRows.Select(row => row.Values["Code"]));

            await using var noKeySource = CreateSource(factory, schema, "No Key Rows");
            await Assert.ThrowsAsync<PostgreSqlDeterministicOrderingUnavailableException>(
                () => ReadAllAsync(noKeySource.ReadAsync(CancellationToken.None)));

            await using var nullableUniqueSource = CreateSource(factory, schema, "Nullable Unique Rows");
            await Assert.ThrowsAsync<PostgreSqlDeterministicOrderingUnavailableException>(
                () => ReadAllAsync(nullableUniqueSource.ReadAsync(CancellationToken.None)));

            var nullsNotDistinctFactory = new CountingConnectionFactory(factory);
            await using var nullsNotDistinctSource = CreateSource(
                nullsNotDistinctFactory,
                schema,
                "Nulls Not Distinct Rows");
            await Assert.ThrowsAsync<PostgreSqlDeterministicOrderingUnavailableException>(
                () => ReadAllAsync(nullsNotDistinctSource.ReadAsync(CancellationToken.None)));
            Assert.Equal(1, nullsNotDistinctFactory.OpenDatabaseCount);

            await using var compositeSource = CreateSource(factory, schema, "Composite Rows");
            var compositeRows = await ReadAllAsync(compositeSource.ReadAsync(CancellationToken.None));
            Assert.Equal(
                [(1, 1), (1, 2), (2, 1)],
                compositeRows.Select(row =>
                    (Assert.IsType<int>(row.Values["First"]), Assert.IsType<int>(row.Values["Second"]))));

            await using var firstValidSource = CreateSource(factory, schema, "First Valid Rows");
            var firstValidRows = await ReadAllAsync(firstValidSource.ReadAsync(CancellationToken.None));
            var session = CreateRowProcessor().CreateSession(new PipelineDefinition
            {
                SourceOptions = new SourceOptions(),
                ExpectedSchema =
                [
                    new SourceFieldDefinition { Name = "Source Id" },
                    new SourceFieldDefinition { Name = "Logical Id" }
                ],
                FieldMappings =
                [
                    new FieldMapping { SourceField = "Source Id", TargetField = "sourceId", IsIncluded = true },
                    new FieldMapping { SourceField = "Logical Id", TargetField = "id", IsIncluded = true }
                ],
                UpsertKeyField = "id"
            });

            var outcomes = firstValidRows.Select(session.Process).ToArray();

            Assert.Equal([1, 2], firstValidRows.Select(row => Assert.IsType<int>(row.Values["Source Id"])));
            Assert.Equal(RowProcessingStatus.Valid, outcomes[0].Status);
            Assert.Equal(RowProcessingStatus.Duplicate, outcomes[1].Status);
        }
        finally
        {
            await ExecuteAsync(setupConnection, "DROP SCHEMA IF EXISTS \"DB9 Ordering\" CASCADE;");
        }
    }

    [Fact]
    public async Task PreviewAsync_ProcessesOnlyTheFirstOneHundredDeterministicallyOrderedRows()
    {
        const string schema = "DB10 Preview";
        const string table = "Preview Rows";
        var factory = CreateFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(
                setupConnection,
                "CREATE SCHEMA \"DB10 Preview\"; " +
                "CREATE TABLE \"DB10 Preview\".\"Preview Rows\" " +
                "(\"Source Id\" integer PRIMARY KEY, \"Kind\" text NOT NULL, \"Logical Id\" text NOT NULL, \"Value\" text NOT NULL); " +
                "INSERT INTO \"DB10 Preview\".\"Preview Rows\" " +
                "SELECT value, " +
                "CASE value WHEN 4 THEN 'filtered' ELSE 'normal' END, " +
                "CASE value WHEN 1 THEN 'A' WHEN 2 THEN 'A' WHEN 3 THEN 'A' ELSE 'K' || value::text END, " +
                "CASE value WHEN 1 THEN '-1' WHEN 5 THEN 'not-a-number' ELSE '10' END " +
                "FROM generate_series(105, 1, -1) AS value;");

            var pipeline = new PipelineDefinition
            {
                SourceType = SourceType.PostgreSql,
                SourceOptions = new SourceOptions { CultureName = "en-US" },
                ExpectedSchema =
                [
                    new SourceFieldDefinition { Name = "Source Id", DataType = SourceFieldType.Integer },
                    new SourceFieldDefinition { Name = "Kind", DataType = SourceFieldType.String },
                    new SourceFieldDefinition { Name = "Logical Id", DataType = SourceFieldType.String },
                    new SourceFieldDefinition { Name = "Value", DataType = SourceFieldType.String }
                ],
                FieldMappings =
                [
                    new FieldMapping { SourceField = "Source Id", TargetField = "sourceId", IsIncluded = true },
                    new FieldMapping { SourceField = "Kind", TargetField = "kind", IsIncluded = true },
                    new FieldMapping { SourceField = "Logical Id", TargetField = "id", IsIncluded = true },
                    new FieldMapping { SourceField = "Value", TargetField = "value", IsIncluded = true }
                ],
                TransformationRules =
                [
                    Rule(1, TransformationType.FilterRow, "kind", ("Operator", FilterOperator.Equals.ToString()), ("Value", "filtered")),
                    Rule(2, TransformationType.ConvertToInteger, "value"),
                    DeduplicateRule(3, "id")
                ],
                ValidationRules =
                [
                    new ValidationRule
                    {
                        Type = ValidationType.NumericRange,
                        Field = "value",
                        Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Minimum"] = "0"
                        }
                    }
                ],
                DestinationDatabase = "demo",
                DestinationCollection = "rows",
                UpsertKeyField = "id"
            };
            var previewSourcePipeline = new PipelineDefinition
            {
                Id = Guid.NewGuid(),
                SourceType = SourceType.PostgreSql,
                SourceOptions = pipeline.SourceOptions,
                ExpectedSchema = pipeline.ExpectedSchema,
                FieldMappings = pipeline.FieldMappings,
                PostgreSqlSource = new PostgreSqlSourceOptions
                {
                    ConnectionProfile = "ReportingDb",
                    Database = GetDatabaseName(),
                    Schema = schema,
                    Table = table
                }
            };
            var previewSourceFactory = new PreviewSourceFactory(
                new UnexpectedWizardSourceStore(),
                factory,
                new PostgreSqlMetadataDiscoveryService(factory),
                new PostgreSqlSourceSchemaConverter(),
                new PostgreSqlDeterministicOrderingResolver(),
                new MongoMetadataDatabase(MongoOptions()),
                MongoOptions(),
                new UnexpectedMongoSchemaInferenceService(),
                new SourceSchemaComparisonService());
            await using var source = await previewSourceFactory.AcquireAsync(
                previewSourcePipeline,
                CancellationToken.None)
                ?? throw new InvalidOperationException("The PostgreSQL preview source was unavailable.");
            var preview = await new PreviewService(
                    new PreviewReadyReadinessService(),
                    new PipelineRowProcessor(
                        new FieldMappingService(),
                        new TransformationEngine(new TransformationHandlerRegistry(
                        [
                            new ConditionalFilterTransformationHandler(),
                            new ConvertToIntegerTransformationHandler(),
                            new DeduplicateTransformationHandler()
                        ])),
                        new ValidationEngine(new ValidationHandlerRegistry([new NumericRangeValidationHandler()]))))
                .PreviewAsync(source, pipeline, CancellationToken.None);

            Assert.Equal(100, preview.Rows.Count);
            Assert.Equal(96, preview.ValidRowCount);
            Assert.Equal(2, preview.InvalidRowCount);
            Assert.Equal(1, preview.FilteredRowCount);
            Assert.Equal(1, preview.DuplicateRowCount);
            Assert.Equal(96, preview.FinalValidRows.Count);
            Assert.Equal(
                Enumerable.Range(1, 100),
                preview.Rows.Select(row => Assert.IsType<int>(row.OriginalRow.Values["Source Id"])));
            Assert.Equal(RowProcessingStatus.Invalid, preview.Rows[0].Status);
            Assert.Equal(RowProcessingStatus.Valid, preview.Rows[1].Status);
            Assert.Equal(RowProcessingStatus.Duplicate, preview.Rows[2].Status);
        }
        finally
        {
            await ExecuteAsync(setupConnection, "DROP SCHEMA IF EXISTS \"DB10 Preview\" CASCADE;");
        }
    }

    [Fact]
    public async Task PreviewSourceFactory_RejectsLiveSchemaDriftBeforeRowEnumeration()
    {
        const string schema = "DB23 Preview Drift";
        const string table = "Customers";
        var factory = CreateFactory();
        await using var setupConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(
                setupConnection,
                "CREATE SCHEMA \"DB23 Preview Drift\"; " +
                "CREATE TABLE \"DB23 Preview Drift\".\"Customers\" (\"Id\" integer PRIMARY KEY);");
            var pipeline = new PipelineDefinition
            {
                Id = Guid.NewGuid(),
                SourceType = SourceType.PostgreSql,
                SourceOptions = new SourceOptions { CultureName = "en-US" },
                ExpectedSchema =
                [
                    new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }
                ],
                FieldMappings =
                [
                    new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true }
                ],
                PostgreSqlSource = new PostgreSqlSourceOptions
                {
                    ConnectionProfile = "ReportingDb",
                    Database = GetDatabaseName(),
                    Schema = schema,
                    Table = table
                }
            };
            await ExecuteAsync(
                setupConnection,
                "ALTER TABLE \"DB23 Preview Drift\".\"Customers\" ADD COLUMN \"Name\" text;");
            var previewSourceFactory = new PreviewSourceFactory(
                new UnexpectedWizardSourceStore(),
                factory,
                new PostgreSqlMetadataDiscoveryService(factory),
                new PostgreSqlSourceSchemaConverter(),
                new PostgreSqlDeterministicOrderingResolver(),
                new MongoMetadataDatabase(MongoOptions()),
                MongoOptions(),
                new UnexpectedMongoSchemaInferenceService(),
                new SourceSchemaComparisonService());

            var exception = await Assert.ThrowsAsync<PostgreSqlSourceSchemaChangedException>(() =>
                previewSourceFactory.AcquireAsync(pipeline, CancellationToken.None));

            Assert.Equal(PostgreSqlSourceSchemaChangedException.SafeMessage, exception.Message);
        }
        finally
        {
            await ExecuteAsync(setupConnection, "DROP SCHEMA IF EXISTS \"DB23 Preview Drift\" CASCADE;");
        }
    }

    private PostgreSqlConnectionFactory CreateFactory() => new(
        new PostgreSqlConnectionOptions
        {
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
            {
                ["ReportingDb"] = new() { ConnectionString = fixture.ConnectionString }
            }
        });

    private string GetDatabaseName() =>
        new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database
        ?? throw new InvalidOperationException(
            "The PostgreSQL test container did not provide a database name.");

    private static MongoDbOptions MongoOptions() => new()
    {
        ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
        MetadataDatabaseName = "etl_tool_postgresql_preview_source_factory_tests"
    };

    private sealed class UnexpectedMongoSchemaInferenceService : IMongoSourceSchemaInferenceService
    {
        public Task<IReadOnlyList<SourceFieldDefinition>> InferAsync(
            MongoDbSourceOptions source,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private PostgreSqlEtlSource CreateSource(
        IPostgreSqlConnectionFactory factory,
        string schema,
        string table) => new(
            factory,
            new PostgreSqlMetadataDiscoveryService(factory),
            new PostgreSqlDeterministicOrderingResolver(),
            new PostgreSqlSourceOptions
            {
                ConnectionProfile = "ReportingDb",
                Database = GetDatabaseName(),
                Schema = schema,
                Table = table
            });

    private static PipelineRowProcessor CreateRowProcessor() => new(
        new FieldMappingService(),
        new TransformationEngine(new TransformationHandlerRegistry([])),
        new ValidationEngine(new ValidationHandlerRegistry([])));

    private static TransformationRule Rule(
        int order,
        TransformationType type,
        string sourceField,
        params (string Key, string Value)[] configuration) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type,
        SourceField = sourceField,
        Configuration = configuration.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal)
    };

    private static TransformationRule DeduplicateRule(int order, string field) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = TransformationType.Deduplicate,
        Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Fields"] = System.Text.Json.JsonSerializer.Serialize(new[] { field })
        }
    };

    private sealed class PreviewReadyReadinessService : IPipelineReadinessService
    {
        public PipelineReadinessResult Evaluate(PipelineDefinition pipeline) => new([]);

        public Task<PipelineReadinessResult?> EvaluateAsync(
            Guid pipelineId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PipelineReadinessResult?>(new PipelineReadinessResult([]));
    }

    private sealed class UnexpectedWizardSourceStore : IWizardSourceStore
    {
        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PostgreSQL preview must not acquire a wizard file source.");

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task ExecuteAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<EtlDataRow>> ReadAllAsync(
        IAsyncEnumerable<EtlDataRow> rows)
    {
        var result = new List<EtlDataRow>();
        await foreach (var row in rows)
        {
            result.Add(row);
        }

        return result;
    }

    private sealed class CountingConnectionFactory(
        IPostgreSqlConnectionFactory inner) : IPostgreSqlConnectionFactory
    {
        public int OpenDatabaseCount { get; private set; }

        public Task<DbConnection> OpenAsync(
            string connectionProfile,
            CancellationToken cancellationToken) =>
            inner.OpenAsync(connectionProfile, cancellationToken);

        public Task<DbConnection> OpenDatabaseAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken)
        {
            OpenDatabaseCount++;
            return inner.OpenDatabaseAsync(connectionProfile, database, cancellationToken);
        }
    }
}
