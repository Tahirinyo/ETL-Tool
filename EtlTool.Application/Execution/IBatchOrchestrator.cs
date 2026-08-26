using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.Processing;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Execution;

public interface IBatchOrchestrator
{
    Task<BatchExecutionResult> ExecuteAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task> processBatchAsync,
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        CancellationToken cancellationToken);

    Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> processBatchAsync,
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        CancellationToken cancellationToken);

    Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> processBatchAsync,
        Func<RowProcessingResult, CancellationToken, Task> reportInvalidRowAsync,
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        CancellationToken cancellationToken);
}
