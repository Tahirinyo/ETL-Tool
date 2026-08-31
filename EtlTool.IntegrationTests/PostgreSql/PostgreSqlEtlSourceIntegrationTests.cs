using System.Data.Common;
using EtlTool.Application.Mapping;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
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
