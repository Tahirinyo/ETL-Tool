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
    public bool HasInspection => Columns.Count > 0 || SampleRows.Count > 0;
}

public sealed class SourceSampleRowViewModel
{
    public long SourceRowNumber { get; init; }
    public IReadOnlyList<string?> Values { get; init; } = [];
}
