using EtlTool.Domain.Entities;

namespace EtlTool.Application.Preview;

public interface IPreviewService
{
    Task<PreviewResult> PreviewAsync(
        Stream source,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);
}
