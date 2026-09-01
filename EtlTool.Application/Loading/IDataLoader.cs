using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Loading;

public interface IDataLoader
{
    DestinationType DestinationType { get; }

    Task PrepareAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);

    Task<BatchLoadResult> UpsertBatchAsync(
        IReadOnlyList<DataRow> rows,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);
}
