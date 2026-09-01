using EtlTool.Application.Connections;
using EtlTool.Domain.Enums;
using MongoDB.Driver;
using Npgsql;

namespace EtlTool.Infrastructure.Connections;

public sealed class SavedConnectionConfigurationValidator : ISavedConnectionConfigurationValidator
{
    public void Validate(DatabaseProviderType providerType, string connectionConfiguration)
    {
        if (string.IsNullOrWhiteSpace(connectionConfiguration))
        {
            throw new SavedConnectionConfigurationException("Connection configuration is required.");
        }

        try
        {
            switch (providerType)
            {
                case DatabaseProviderType.MongoDb:
                    _ = new MongoUrl(connectionConfiguration);
                    break;
                case DatabaseProviderType.PostgreSql:
                    var builder = new NpgsqlConnectionStringBuilder(connectionConfiguration);
                    if (string.IsNullOrWhiteSpace(builder.Host))
                    {
                        throw new ArgumentException();
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(providerType));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or MongoConfigurationException)
        {
            throw new SavedConnectionConfigurationException(
                $"The {ProviderDisplay(providerType)} connection configuration is malformed.");
        }
    }

    private static string ProviderDisplay(DatabaseProviderType providerType) => providerType switch
    {
        DatabaseProviderType.MongoDb => "MongoDB",
        DatabaseProviderType.PostgreSql => "PostgreSQL",
        _ => "database"
    };
}
