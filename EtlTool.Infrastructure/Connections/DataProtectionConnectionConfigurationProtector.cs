using EtlTool.Application.Connections;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace EtlTool.Infrastructure.Connections;

public interface IConnectionConfigurationProtector
{
    string Protect(string connectionConfiguration);

    string Unprotect(string protectedConfiguration);
}

public sealed class DataProtectionConnectionConfigurationProtector : IConnectionConfigurationProtector
{
    public const string Purpose = "EtlTool.SavedDatabaseConnections.ConnectionConfiguration.v1";

    private readonly IDataProtector _protector;

    public DataProtectionConnectionConfigurationProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string connectionConfiguration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionConfiguration);
        return _protector.Protect(connectionConfiguration);
    }

    public string Unprotect(string protectedConfiguration)
    {
        if (string.IsNullOrWhiteSpace(protectedConfiguration))
        {
            throw new SavedConnectionResolutionException();
        }

        try
        {
            return _protector.Unprotect(protectedConfiguration);
        }
        catch (CryptographicException exception)
        {
            throw new SavedConnectionResolutionException(exception);
        }
    }
}
