using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Preview;

public interface IPreviewService
{
    Task<PreviewResult> PreviewAsync(
        IEtlSource source,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);
}
