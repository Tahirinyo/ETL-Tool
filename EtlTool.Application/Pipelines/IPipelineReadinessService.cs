namespace EtlTool.Application.Pipelines;

public interface IPipelineReadinessService
{
    Task<PipelineReadinessResult?> EvaluateAsync(
        Guid pipelineId,
        CancellationToken cancellationToken);
}
