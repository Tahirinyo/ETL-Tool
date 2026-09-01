using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Processing;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Execution;

public interface IBatchOrchestrator
{
    Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
        IEtlSource source,
        PipelineDefinition pipeline,
        IDataLoader loader,
        Func<RowProcessingResult, CancellationToken, Task> reportInvalidRowAsync,
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        CancellationToken cancellationToken);
}
