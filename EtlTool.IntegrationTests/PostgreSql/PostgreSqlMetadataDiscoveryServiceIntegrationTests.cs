using System.Data.Common;
using EtlTool.Application.PostgreSql;
using EtlTool.Infrastructure.PostgreSql;

namespace EtlTool.IntegrationTests.PostgreSql;

[Collection(PostgreSqlTestCollection.CollectionName)]
public sealed class PostgreSqlMetadataDiscoveryServiceIntegrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task DiscoverKeyConstraintsAsync_PreservesCompositeOrderNullabilityAndQuotedNames()
    {
        const string schema = "DB9 \"Metadata";
        const string table = "Rows \"Table";
        var factory = CreateFactory();
        var discovery = new PostgreSqlMetadataDiscoveryService(factory);
        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(
                connection,
                "CREATE SCHEMA \"DB9 \"\"Metadata\"; " +
                "CREATE TABLE \"DB9 \"\"Metadata\".\"Rows \"\"Table\" (" +
                "\"Tenant Id\" integer NOT NULL, \"Row \"\"Id\" integer NOT NULL, \"Email\" text NOT NULL, \"Nulls Not Distinct Email\" text NOT NULL, \"Alias\" text, " +
                "CONSTRAINT \"PK \"\"Rows\" PRIMARY KEY (\"Tenant Id\", \"Row \"\"Id\"), " +
                "CONSTRAINT \"UX Email\" UNIQUE (\"Email\"), " +
                "CONSTRAINT \"UX Tenant Email\" UNIQUE (\"Tenant Id\", \"Email\"), " +
                "CONSTRAINT \"UX Nulls Not Distinct Email\" UNIQUE NULLS NOT DISTINCT (\"Nulls Not Distinct Email\"), " +
                "CONSTRAINT \"UX Alias\" UNIQUE (\"Alias\")); " +
                "CREATE UNIQUE INDEX \"Bare Unique Index\" ON \"DB9 \"\"Metadata\".\"Rows \"\"Table\" (\"Tenant Id\", \"Email\");");

            var constraints = await discovery.DiscoverKeyConstraintsAsync(
                "ReportingDb",
                GetDatabaseName(),
                schema,
                table,
                CancellationToken.None);

            Assert.Equal(5, constraints.Count);
            var primaryKey = Assert.Single(
                constraints,
                constraint => constraint.Kind == PostgreSqlKeyConstraintKind.PrimaryKey);
            Assert.Equal("PK \"Rows", primaryKey.Name);
            Assert.Equal(["Tenant Id", "Row \"Id"], primaryKey.Columns.Select(column => column.Name));
            Assert.Equal([1, 2], primaryKey.Columns.Select(column => column.KeyOrdinal));
            Assert.All(primaryKey.Columns, column => Assert.False(column.IsNullable));

            var email = Assert.Single(constraints, constraint => constraint.Name == "UX Email");
            Assert.Equal(PostgreSqlKeyConstraintKind.Unique, email.Kind);
            Assert.False(email.IsNullsNotDistinct);
            Assert.False(Assert.Single(email.Columns).IsNullable);
            var compositeUnique = Assert.Single(constraints, constraint => constraint.Name == "UX Tenant Email");
            Assert.Equal(["Tenant Id", "Email"], compositeUnique.Columns.Select(column => column.Name));
            Assert.Equal([1, 2], compositeUnique.Columns.Select(column => column.KeyOrdinal));
            Assert.All(compositeUnique.Columns, column => Assert.False(column.IsNullable));
            Assert.DoesNotContain(constraints, constraint => constraint.Name == "Bare Unique Index");
            Assert.True(Assert.Single(constraints, constraint => constraint.Name == "UX Alias").Columns[0].IsNullable);
            Assert.True(Assert.Single(
                constraints,
                constraint => constraint.Name == "UX Nulls Not Distinct Email").IsNullsNotDistinct);
        }
        finally
        {
            await ExecuteAsync(connection, "DROP SCHEMA IF EXISTS \"DB9 \"\"Metadata\" CASCADE;");
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
        ?? throw new InvalidOperationException("The PostgreSQL test container did not provide a database name.");

    private static async Task ExecuteAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}
