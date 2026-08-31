using System.Data.Common;
using EtlTool.Application.Mapping;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
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
                "(\"Id Number\" integer NOT NULL, \"Display Name\" text, \"Amount\" numeric, \"Is Active\" boolean); " +
                "INSERT INTO \"DB8 \"\"Mixed Schema\".\"Rows \"\"Table\" VALUES " +
                "(1, ' Ada ', 12.50, TRUE), (2, NULL, NULL, FALSE), (3, 'Linus', 7.25, TRUE);");

            await using var source = new PostgreSqlEtlSource(factory, options);
            options.Schema = "Changed";
            options.Table = "Changed";

            var rows = await ReadAllAsync(source.ReadAsync(CancellationToken.None));

            Assert.Equal(3, rows.Count);
            Assert.Equal([1L, 2L, 3L], rows.Select(row => row.SourceRowNumber));
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
}
