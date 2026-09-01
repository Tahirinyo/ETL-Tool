using System.ComponentModel.DataAnnotations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Web.Models.Connections;

public sealed class SavedConnectionListItemViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DatabaseProviderType ProviderType { get; init; }
    public int ActiveRevision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public string ProviderDisplay => ProviderType == DatabaseProviderType.PostgreSql
        ? "PostgreSQL"
        : "MongoDB";

    public static SavedConnectionListItemViewModel From(SavedDatabaseConnection connection) => new()
    {
        Id = connection.Id,
        Name = connection.Name,
        ProviderType = connection.ProviderType,
        ActiveRevision = connection.ActiveRevision,
        UpdatedAt = connection.UpdatedAt
    };
}

public sealed class CreateSavedConnectionViewModel
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Database type")]
    public DatabaseProviderType ProviderType { get; set; }

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Connection string")]
    public string ConnectionConfiguration { get; set; } = string.Empty;
}

public sealed class EditSavedConnectionViewModel
{
    public Guid Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public DatabaseProviderType ProviderType { get; set; }

    public int ActiveRevision { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Replacement connection string")]
    public string? ConnectionConfiguration { get; set; }

    public string ProviderDisplay => ProviderType == DatabaseProviderType.PostgreSql
        ? "PostgreSQL"
        : "MongoDB";
}

public sealed class DeleteSavedConnectionViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ProviderDisplay { get; init; } = string.Empty;
    public string? FailureMessage { get; init; }
}
