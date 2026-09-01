using EtlTool.Application.Connections;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.Connections;
using Microsoft.AspNetCore.DataProtection;

namespace EtlTool.UnitTests.Infrastructure.Connections;

public sealed class SavedConnectionProtectionTests
{
    [Fact]
    public void PersistedKeyRing_AllowsProtectionProviderReload()
    {
        var keyDirectory = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(), $"etl-tool-connection-keys-{Guid.NewGuid():N}"));
        try
        {
            const string secret = "Host=server;Password=persisted-key-ring-secret";
            var first = new DataProtectionConnectionConfigurationProtector(
                DataProtectionProvider.Create(keyDirectory));
            var protectedValue = first.Protect(secret);

            var reloaded = new DataProtectionConnectionConfigurationProtector(
                DataProtectionProvider.Create(keyDirectory));

            Assert.Equal(secret, reloaded.Unprotect(protectedValue));
            Assert.NotEmpty(keyDirectory.GetFiles());
        }
        finally
        {
            if (keyDirectory.Exists)
            {
                keyDirectory.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void Protect_RoundTripsWithoutPlaintextPayload()
    {
        const string secret = "mongodb://user:distinctive-password@server:27017";
        var protector = new DataProtectionConnectionConfigurationProtector(
            new EphemeralDataProtectionProvider());

        var protectedValue = protector.Protect(secret);

        Assert.DoesNotContain(secret, protectedValue, StringComparison.Ordinal);
        Assert.DoesNotContain("distinctive-password", protectedValue, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_TamperedPayload_FailsWithSafeMessage()
    {
        var protector = new DataProtectionConnectionConfigurationProtector(
            new EphemeralDataProtectionProvider());
        var protectedValue = protector.Protect("Host=server;Password=distinctive-password");

        var exception = Assert.Throws<SavedConnectionResolutionException>(() =>
            protector.Unprotect(protectedValue + "tampered"));

        Assert.Equal("The saved database connection is unavailable.", exception.Message);
        Assert.DoesNotContain("distinctive-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseProviderType.MongoDb, "mongodb://localhost:27017")]
    [InlineData(DatabaseProviderType.PostgreSql, "Host=localhost;Database=etl;Username=user;Password=password")]
    public void Validator_AcceptsSupportedProviderSyntax(
        DatabaseProviderType providerType,
        string configuration)
    {
        new SavedConnectionConfigurationValidator().Validate(providerType, configuration);
    }

    [Theory]
    [InlineData(DatabaseProviderType.MongoDb, "not a mongodb url")]
    [InlineData(DatabaseProviderType.PostgreSql, "UnknownKeyword=value")]
    public void Validator_RejectsMalformedConfigurationWithoutEchoingIt(
        DatabaseProviderType providerType,
        string configuration)
    {
        var exception = Assert.Throws<SavedConnectionConfigurationException>(() =>
            new SavedConnectionConfigurationValidator().Validate(providerType, configuration));

        Assert.DoesNotContain(configuration, exception.Message, StringComparison.Ordinal);
    }
}
