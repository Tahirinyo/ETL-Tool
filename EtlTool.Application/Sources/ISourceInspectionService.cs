using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Sources;

public interface ISourceInspectionService
{
    Task<SourceInspectionResult> InspectCsvAsync(Stream content, string fileName, SourceOptions options, CancellationToken cancellationToken);
    Task<SourceInspectionResult> StageXlsxAsync(Stream content, string fileName, CancellationToken cancellationToken);
    Task<SourceInspectionResult> InspectStagedXlsxAsync(Guid stageId, string worksheetName, CancellationToken cancellationToken);
}

public sealed class SourceInspectionResult
{
    public SourceType SourceType { get; init; }
    public IReadOnlyList<string> WorksheetNames { get; init; } = [];
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<DataRow> SampleRows { get; init; } = [];
    public Guid? StageId { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsSuccess => ErrorMessage is null;
}
