using EtlTool.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace EtlTool.Web.Models.Pipelines;

public sealed class SourceUploadViewModel
{
    public SourceType SourceType { get; set; } = SourceType.Csv;
    public IFormFile? SourceFile { get; set; }
    public CsvDelimiter Delimiter { get; set; } = CsvDelimiter.Comma;
    public string? WorksheetName { get; set; }
    public Guid? StageId { get; set; }
    public IReadOnlyList<string> WorksheetNames { get; set; } = [];
    public IReadOnlyList<string> Columns { get; set; } = [];
    public IReadOnlyList<SourceSampleRowViewModel> SampleRows { get; set; } = [];
    public SchemaDifferenceViewModel? SchemaDifference { get; set; }
    public Guid? PendingSourceReferenceId { get; set; }
    public string? PostgreSqlConnectionProfile { get; set; }
    public string? PostgreSqlDatabase { get; set; }
    public string? PostgreSqlSchema { get; set; }
    public string? PostgreSqlTable { get; set; }
    public string? LoadedPostgreSqlConnectionProfile { get; set; }
    public string? LoadedPostgreSqlDatabase { get; set; }
    public string? LoadedPostgreSqlSchema { get; set; }
    public IReadOnlyList<string> PostgreSqlConnectionProfiles { get; set; } = [];
    public IReadOnlyList<string> PostgreSqlDatabases { get; set; } = [];
    public IReadOnlyList<string> PostgreSqlSchemas { get; set; } = [];
    public IReadOnlyList<string> PostgreSqlTables { get; set; } = [];
    public string? MongoDbDatabase { get; set; }
    public string? MongoDbCollection { get; set; }
    public string? LoadedMongoDbDatabase { get; set; }
    public IReadOnlyList<string> MongoDbDatabases { get; set; } = [];
    public IReadOnlyList<string> MongoDbCollections { get; set; } = [];
    public bool HasInspection => Columns.Count > 0 || SampleRows.Count > 0;
}

public sealed class SchemaDifferenceViewModel
{
    public IReadOnlyList<SchemaFieldViewModel> MissingFields { get; init; } = [];
    public IReadOnlyList<SchemaFieldViewModel> NewFields { get; init; } = [];
    public IReadOnlyList<SchemaTypeChangeViewModel> TypeChanges { get; init; } = [];
    public IReadOnlyList<UnresolvedMappingViewModel> UnresolvedMappings { get; init; } = [];
    public bool RequiresRemapping => MissingFields.Count > 0
        || NewFields.Count > 0
        || TypeChanges.Count > 0
        || UnresolvedMappings.Count > 0;
}

public sealed class SchemaFieldViewModel
{
    public string Name { get; init; } = string.Empty;
    public SourceFieldType DataType { get; init; }
}

public sealed class SchemaTypeChangeViewModel
{
    public string FieldName { get; init; } = string.Empty;
    public SourceFieldType SavedType { get; init; }
    public SourceFieldType InspectedType { get; init; }
}

public sealed class UnresolvedMappingViewModel
{
    public string SourceField { get; init; } = string.Empty;
    public string TargetField { get; init; } = string.Empty;
    public bool IsIncluded { get; init; }
}

public sealed class SourceSampleRowViewModel
{
    public long SourceRowNumber { get; init; }
    public IReadOnlyList<string?> Values { get; init; } = [];
}
