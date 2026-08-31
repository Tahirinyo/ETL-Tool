using EtlTool.Domain.Entities;

namespace EtlTool.Application.Pipelines;

public interface IPipelineReadinessService
{
    PipelineReadinessResult Evaluate(PipelineDefinition pipeline);

    PipelineReadinessResult EvaluateForPreview(PipelineDefinition pipeline) => Evaluate(pipeline);

    Task<PipelineReadinessResult?> EvaluateAsync(
        Guid pipelineId,
        CancellationToken cancellationToken);
}
