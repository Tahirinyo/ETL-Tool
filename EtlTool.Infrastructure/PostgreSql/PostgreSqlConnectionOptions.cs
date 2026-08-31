using Npgsql;

namespace EtlTool.Infrastructure.PostgreSql;

public sealed class PostgreSqlConnectionOptions
{
    public const string SectionName = "PostgreSql";

    public Dictionary<string, PostgreSqlConnectionProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (Profiles is null)
        {
            throw new InvalidOperationException(
                $"Configuration section '{SectionName}:Profiles' is invalid.");
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileName, profile) in Profiles)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidOperationException(
                    $"Configuration section '{SectionName}:Profiles' contains an unnamed profile.");
            }

            if (!profileNames.Add(profileName))
            {
                throw new InvalidOperationException(
                    $"Configuration section '{SectionName}:Profiles' contains duplicate profile names.");
            }

            if (profile is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL connection profile '{profileName}' is invalid.");
            }

            profile.Validate(profileName);
        }
    }
}

public sealed class PostgreSqlConnectionProfileOptions
{
    // This value is configuration-only and must never be copied into pipeline or run data.
    public string ConnectionString { get; set; } = string.Empty;

    internal void Validate(string profileName)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"PostgreSQL connection profile '{profileName}' requires a connection string. " +
                "Configure it through an environment variable, user secrets, or another secret configuration provider.");
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            if (builder.Timeout < 1)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL connection profile '{profileName}' must configure a bounded connection timeout.");
            }
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"PostgreSQL connection profile '{profileName}' has invalid connection configuration.");
        }
    }
}
