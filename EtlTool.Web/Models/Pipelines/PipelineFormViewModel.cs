using System.ComponentModel.DataAnnotations;
using EtlTool.Domain.Enums;

namespace EtlTool.Web.Models.Pipelines;

public sealed class PipelineFormViewModel
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Display(Name = "Destination")]
    public DestinationType DestinationType { get; set; } = DestinationType.MongoDb;

    [Display(Name = "Destination database")]
    public string? DestinationDatabase { get; set; }

    [Display(Name = "Destination collection")]
    public string? DestinationCollection { get; set; }

    [Display(Name = "Upsert key")]
    public string? UpsertKeyField { get; set; }

    public IReadOnlyList<string> AvailableMappedFields { get; set; } = [];

    [Display(Name = "PostgreSQL connection profile")]
    public string? PostgreSqlConnectionProfile { get; set; }

    [Display(Name = "PostgreSQL database")]
    public string? PostgreSqlDatabase { get; set; }

    [Display(Name = "PostgreSQL schema")]
    public string? PostgreSqlSchema { get; set; }

    [Display(Name = "PostgreSQL table")]
    public string? PostgreSqlTable { get; set; }

    public string? LoadedPostgreSqlConnectionProfile { get; set; }

    public string? LoadedPostgreSqlDatabase { get; set; }

    public string? LoadedPostgreSqlSchema { get; set; }

    public IReadOnlyList<string> PostgreSqlConnectionProfiles { get; set; } = [];

    public IReadOnlyList<string> PostgreSqlDatabases { get; set; } = [];

    public IReadOnlyList<string> PostgreSqlSchemas { get; set; } = [];

    public IReadOnlyList<string> PostgreSqlTables { get; set; } = [];

    public IReadOnlyList<PostgreSqlDestinationColumnViewModel> PostgreSqlColumns { get; set; } = [];

    public List<PostgreSqlDestinationMappingViewModel> PostgreSqlColumnMappings { get; set; } = [];

    [Display(Name = "PostgreSQL upsert key")]
    public string? PostgreSqlUpsertKeyColumn { get; set; }

    public IReadOnlyList<string> PostgreSqlUpsertKeyColumns { get; set; } = [];

    public string? PostgreSqlDestinationAction { get; set; }
}

public sealed class PostgreSqlDestinationColumnViewModel
{
    public string Name { get; init; } = string.Empty;

    public string NativeType { get; init; } = string.Empty;

    public bool IsNullable { get; init; }
}

public sealed class PostgreSqlDestinationMappingViewModel
{
    public string? OutputField { get; set; }

    public string? DestinationColumn { get; set; }
}
