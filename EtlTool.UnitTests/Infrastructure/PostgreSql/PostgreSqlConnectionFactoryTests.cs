using EtlTool.Application.PostgreSql;
using EtlTool.Infrastructure.PostgreSql;

namespace EtlTool.UnitTests.Infrastructure.PostgreSql;

public sealed class PostgreSqlConnectionFactoryTests
{
    [Fact]
    public void GetProfileNames_ExposesOnlyLogicalNames()
    {
        IPostgreSqlConnectionProfileCatalog catalog = CreateFactory();

        var profileNames = catalog.GetProfileNames();

        Assert.Equal(["ReportingDb"], profileNames);
        Assert.DoesNotContain("test-only-password", string.Join(',', profileNames), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_RejectsUnknownProfileWithoutDisclosingConfiguredSecret()
    {
        var factory = CreateFactory();

        var exception = await Assert.ThrowsAsync<PostgreSqlConnectionProfileNotFoundException>(
            () => factory.OpenAsync("UnknownProfile", CancellationToken.None));

        Assert.Equal(
            "The configured PostgreSQL connection profile was not found.",
            exception.Message);
        Assert.DoesNotContain("test-only-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_TranslatesUnreachableDatabaseFailureWithoutDisclosingSecret()
    {
        var factory = CreateFactory();

        var exception = await Assert.ThrowsAsync<PostgreSqlConnectionAccessException>(
            () => factory.OpenAsync("ReportingDb", CancellationToken.None));

        Assert.Equal("The configured PostgreSQL source could not be accessed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("test-only-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_PropagatesCancellation()
    {
        var factory = CreateFactory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.OpenAsync("ReportingDb", cancellation.Token));
    }

    [Fact]
    public async Task DiscoverDatabasesAsync_PropagatesCancellationThroughTheProfileFactory()
    {
        var discovery = new PostgreSqlMetadataDiscoveryService(CreateFactory());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.DiscoverDatabasesAsync("ReportingDb", cancellation.Token));
    }

    [Fact]
    public async Task DiscoverSchemasAsync_TranslatesUnreachableProfileConnectionWithoutDisclosingSecret()
    {
        var discovery = new PostgreSqlMetadataDiscoveryService(CreateFactory());

        var exception = await Assert.ThrowsAsync<PostgreSqlConnectionAccessException>(
            () => discovery.DiscoverSchemasAsync("ReportingDb", "reporting", CancellationToken.None));

        Assert.Equal("The configured PostgreSQL source could not be accessed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("test-only-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RejectUnboundedConnectionTimeoutWithoutDisclosingSecret()
    {
        var options = new PostgreSqlConnectionOptions
        {
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
            {
                ["ReportingDb"] = new()
                {
                    ConnectionString = "Host=localhost;Password=test-only-password;Timeout=0"
                }
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.DoesNotContain("test-only-password", exception.ToString(), StringComparison.Ordinal);
    }

    private static PostgreSqlConnectionFactory CreateFactory() => new(
        new PostgreSqlConnectionOptions
        {
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
            {
                ["ReportingDb"] = new()
                {
                    ConnectionString =
                        "Host=127.0.0.1;Port=1;Database=postgres;Username=etl_test;" +
                        "Password=test-only-password;Timeout=1;Pooling=false;SSL Mode=Disable"
                }
            }
        });
}
