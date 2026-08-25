using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Execution;

public interface IBatchOrchestrator
{
    Task<BatchExecutionResult> ExecuteAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task> processBatchAsync,
        CancellationToken cancellationToken);
}
