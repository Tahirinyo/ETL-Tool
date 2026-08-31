using EtlTool.Application.PostgreSql;
using EtlTool.Infrastructure.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtlTool.UnitTests.Infrastructure.PostgreSql;

public sealed class PostgreSqlConfigurationTests
{
    [Fact]
    public async Task EnvironmentStyleProfileConfiguration_BindsAndResolvesThroughDi()
    {
        const string environmentVariableName =
            "ETLTOOL_DB4_PostgreSql__Profiles__ReportingDb__ConnectionString";
        const string connectionString =
            "Host=127.0.0.1;Port=1;Database=reporting;Username=etl_test;" +
            "Password=test-only-password;Timeout=1;Pooling=false;SSL Mode=Disable";

        Environment.SetEnvironmentVariable(environmentVariableName, connectionString);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix: "ETLTOOL_DB4_")
                .Build();
            var options = configuration
                .GetSection(PostgreSqlConnectionOptions.SectionName)
                .Get<PostgreSqlConnectionOptions>();

            Assert.NotNull(options);
            options.Validate();

            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddSingleton<IPostgreSqlConnectionFactory, PostgreSqlConnectionFactory>();
            services.AddSingleton<IPostgreSqlMetadataDiscoveryService, PostgreSqlMetadataDiscoveryService>();
            using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IPostgreSqlConnectionFactory>();
            Assert.IsType<PostgreSqlMetadataDiscoveryService>(
                provider.GetRequiredService<IPostgreSqlMetadataDiscoveryService>());
            var exception = await Assert.ThrowsAsync<PostgreSqlConnectionAccessException>(
                () => factory.OpenAsync("REPORTINGDB", CancellationToken.None));

            Assert.DoesNotContain("test-only-password", exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariableName, null);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsWhitespaceProfileWithoutTryingAnotherConfiguredProfile()
    {
        var factory = new PostgreSqlConnectionFactory(
            new PostgreSqlConnectionOptions
            {
                Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
                {
                    ["Default"] = new()
                    {
                        ConnectionString = "Host=127.0.0.1;Port=1;Database=reporting;Timeout=1"
                    }
                }
            });

        await Assert.ThrowsAsync<ArgumentException>(
            () => factory.OpenAsync("  ", CancellationToken.None));
    }

    [Fact]
    public void Options_RejectMissingProfileConnectionStringWithoutDisclosingOtherProfileSecret()
    {
        var options = new PostgreSqlConnectionOptions
        {
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>
            {
                ["ReportingDb"] = new() { ConnectionString = "Host=localhost;Password=test-only-password" },
                ["Missing"] = new()
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.DoesNotContain("test-only-password", exception.ToString(), StringComparison.Ordinal);
    }
}
