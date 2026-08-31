using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Sources;

/// <summary>
/// Acquires the persisted source required for an interactive pipeline preview.
/// </summary>
public interface IPreviewSourceFactory
{
    Task<IEtlSource?> AcquireAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);
}
