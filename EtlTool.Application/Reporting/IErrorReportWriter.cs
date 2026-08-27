using EtlTool.Application.Processing;

namespace EtlTool.Application.Reporting;

/// <summary>
/// Writes row-processing failures for one ETL run to a caller-owned report stream.
/// </summary>
public interface IErrorReportWriter
{
    Task WriteAsync(
        Stream output,
        Guid runId,
        IReadOnlyList<string> sourceFields,
        string upsertKeyField,
        IAsyncEnumerable<RowProcessingResult> rowResults,
        CancellationToken cancellationToken);
}
