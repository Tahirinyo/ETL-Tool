using System.Data;
using System.Data.Common;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.PostgreSql;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EtlTool.IntegrationTests.PostgreSql;

[CollectionDefinition(CollectionName)]
public sealed class PostgreSqlTestCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string CollectionName = "PostgreSQL connection factory";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string Image = "postgres:18.6";
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Collection(PostgreSqlTestCollection.CollectionName)]
public sealed class PostgreSqlConnectionFactoryIntegrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task OpenAsync_UsesNamedProfileAndTransfersOpenConnectionOwnershipToCaller()
    {
        var factory = new PostgreSqlConnectionFactory(
            new PostgreSqlConnectionOptions
            {
                Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
                {
                    ["ReportingDb"] = new() { ConnectionString = fixture.ConnectionString }
                }
            });

        var connection = await factory.OpenAsync("reportingdb", CancellationToken.None);
        try
        {
            Assert.Equal(ConnectionState.Open, connection.State);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            await connection.DisposeAsync();
        }

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Discovery_UsesProfileDatabaseSelectionAndPreservesQuotedMetadataNames()
    {
        const string schema = "DB5 Mixed Schema";
        const string table = "Select Space";
        var database = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database
            ?? throw new InvalidOperationException("The PostgreSQL test container did not provide a database name.");
        var factory = new PostgreSqlConnectionFactory(
            new PostgreSqlConnectionOptions
            {
                Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
                {
                    ["ReportingDb"] = new() { ConnectionString = fixture.ConnectionString }
                }
            });
        var discovery = new PostgreSqlMetadataDiscoveryService(factory);

        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(
                connection,
                "CREATE SCHEMA \"DB5 Mixed Schema\"; " +
                "CREATE TABLE \"DB5 Mixed Schema\".\"A Table\" (id integer); " +
                "CREATE TABLE \"DB5 Mixed Schema\".\"Select Space\" " +
                "(\"Small Value\" smallint NOT NULL, \"Id Number\" integer NOT NULL, " +
                "\"Large Value\" bigint NOT NULL, \"Plain Numeric\" numeric, " +
                "\"Scaled Numeric\" numeric(12,3), \"Real Value\" real, " +
                "\"Double Value\" double precision, \"Is Active\" boolean, " +
                "\"Birth Date\" date, \"Occurred Local\" timestamp without time zone, " +
                "\"created at\" timestamp with time zone NOT NULL, \"Varchar Value\" varchar(32), " +
                "\"Display Name\" character varying(64), \"Fixed Code\" char(3), \"Notes\" text); " +
                "CREATE TABLE \"DB5 Mixed Schema\".\"Z Table\" (id integer);");

            var databases = await discovery.DiscoverDatabasesAsync("ReportingDb", CancellationToken.None);
            var schemas = await discovery.DiscoverSchemasAsync("ReportingDb", database, CancellationToken.None);
            var tables = await discovery.DiscoverTablesAsync(
                "ReportingDb", database, schema, CancellationToken.None);
            var columns = await discovery.DiscoverColumnsAsync(
                "ReportingDb", database, schema, table, CancellationToken.None);

            Assert.Contains(databases, item => item.Name == database);
            Assert.Contains(schemas, item => item.Name == schema);
            Assert.Equal(
                ["A Table", table, "Z Table"],
                tables.Select(item => item.Name));
            Assert.Equal(
                [
                    new PostgreSqlColumnMetadata("Small Value", "smallint", false, 1),
                    new PostgreSqlColumnMetadata("Id Number", "integer", false, 2),
                    new PostgreSqlColumnMetadata("Large Value", "bigint", false, 3),
                    new PostgreSqlColumnMetadata("Plain Numeric", "numeric", true, 4),
                    new PostgreSqlColumnMetadata("Scaled Numeric", "numeric(12,3)", true, 5),
                    new PostgreSqlColumnMetadata("Real Value", "real", true, 6),
                    new PostgreSqlColumnMetadata("Double Value", "double precision", true, 7),
                    new PostgreSqlColumnMetadata("Is Active", "boolean", true, 8),
                    new PostgreSqlColumnMetadata("Birth Date", "date", true, 9),
                    new PostgreSqlColumnMetadata("Occurred Local", "timestamp without time zone", true, 10),
                    new PostgreSqlColumnMetadata("created at", "timestamp with time zone", false, 11),
                    new PostgreSqlColumnMetadata("Varchar Value", "character varying(32)", true, 12),
                    new PostgreSqlColumnMetadata("Display Name", "character varying(64)", true, 13),
                    new PostgreSqlColumnMetadata("Fixed Code", "character(3)", true, 14),
                    new PostgreSqlColumnMetadata("Notes", "text", true, 15)
                ],
                columns);

            var schemaSnapshot = new PostgreSqlSourceSchemaConverter().Convert(columns);
            Assert.Equal(
                [
                    SourceFieldType.Integer, SourceFieldType.Integer, SourceFieldType.Integer,
                    SourceFieldType.Decimal, SourceFieldType.Decimal, SourceFieldType.Decimal,
                    SourceFieldType.Decimal, SourceFieldType.Boolean, SourceFieldType.Date,
                    SourceFieldType.Date, SourceFieldType.Date, SourceFieldType.String,
                    SourceFieldType.String, SourceFieldType.String, SourceFieldType.String
                ],
                schemaSnapshot.Select(field => field.DataType));

            var exception = await Assert.ThrowsAsync<PostgreSqlMetadataObjectNotFoundException>(
                () => discovery.DiscoverTablesAsync(
                    "ReportingDb", database, "Missing Schema", CancellationToken.None));
            Assert.Equal("schema", exception.ObjectType);
            Assert.DoesNotContain(fixture.ConnectionString, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(connection, "DROP SCHEMA IF EXISTS \"DB5 Mixed Schema\" CASCADE;");
        }
    }

    [Fact]
    public async Task Discovery_UsesAnUnusualSelectedDatabaseWithoutChangingTheNamedProfile()
    {
        const string selectedDatabase = "DB5 Mixed Database";
        var configuredDatabase = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database
            ?? throw new InvalidOperationException("The PostgreSQL test container did not provide a database name.");
        var factory = CreateFactory(
            new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Pooling = false
            }.ConnectionString);
        var discovery = new PostgreSqlMetadataDiscoveryService(factory);

        await using var connection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        await ExecuteAsync(connection, "CREATE DATABASE \"DB5 Mixed Database\";");
        try
        {
            var schemas = await discovery.DiscoverSchemasAsync(
                "ReportingDb", selectedDatabase, CancellationToken.None);

            Assert.Contains(schemas, schema => schema.Name == "public");

            await using var configuredConnection = await factory.OpenAsync(
                "ReportingDb", CancellationToken.None);
            Assert.Equal(configuredDatabase, configuredConnection.Database);
        }
        finally
        {
            await ExecuteAsync(connection, "DROP DATABASE IF EXISTS \"DB5 Mixed Database\";");
        }
    }

    [Fact]
    public async Task Discovery_ReportsMissingTableAndDoesNotBypassRestrictedSchemaPermissions()
    {
        const string schema = "DB5 Private Schema";
        const string role = "db5_restricted";
        const string restrictedPassword = "test-only-restricted-password";
        var database = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database
            ?? throw new InvalidOperationException("The PostgreSQL test container did not provide a database name.");
        var factory = CreateFactory();
        var discovery = new PostgreSqlMetadataDiscoveryService(factory);

        await using var administratorConnection = await factory.OpenAsync("ReportingDb", CancellationToken.None);
        try
        {
            await ExecuteAsync(
                administratorConnection,
                "CREATE ROLE db5_restricted LOGIN PASSWORD 'test-only-restricted-password'; " +
                "CREATE SCHEMA \"DB5 Private Schema\"; " +
                "CREATE TABLE \"DB5 Private Schema\".\"Private Table\" (id integer);");

            var missingTable = await Assert.ThrowsAsync<PostgreSqlMetadataObjectNotFoundException>(
                () => discovery.DiscoverColumnsAsync(
                    "ReportingDb", database, schema, "Missing Table", CancellationToken.None));
            Assert.Equal("table", missingTable.ObjectType);

            var restrictedConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = role,
                Password = restrictedPassword,
                Pooling = false
            }.ConnectionString;
            var restrictedDiscovery = new PostgreSqlMetadataDiscoveryService(
                new PostgreSqlConnectionFactory(
                    new PostgreSqlConnectionOptions
                    {
                        Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
                        {
                            ["Restricted"] = new() { ConnectionString = restrictedConnectionString }
                        }
                    }));

            var schemas = await restrictedDiscovery.DiscoverSchemasAsync(
                "Restricted", database, CancellationToken.None);
            Assert.DoesNotContain(schemas, item => item.Name == schema);

            var inaccessibleSchema = await Assert.ThrowsAsync<PostgreSqlMetadataObjectNotFoundException>(
                () => restrictedDiscovery.DiscoverTablesAsync(
                    "Restricted", database, schema, CancellationToken.None));
            Assert.Equal("schema", inaccessibleSchema.ObjectType);
            Assert.DoesNotContain(restrictedPassword, inaccessibleSchema.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.ConnectionString, inaccessibleSchema.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(administratorConnection, "DROP SCHEMA IF EXISTS \"DB5 Private Schema\" CASCADE;");
            await ExecuteAsync(administratorConnection, "DROP ROLE IF EXISTS db5_restricted;");
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private PostgreSqlConnectionFactory CreateFactory(string? connectionString = null) => new(
        new PostgreSqlConnectionOptions
        {
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
            {
                ["ReportingDb"] = new() { ConnectionString = connectionString ?? fixture.ConnectionString }
            }
        });
}
