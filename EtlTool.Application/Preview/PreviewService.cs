using EtlTool.Application.Extraction;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Preview;

public sealed class PreviewService : IPreviewService
{
    private const int MaximumSourceRows = 100;

    private readonly IPipelineReadinessService _readinessService;
    private readonly PipelineRowProcessor _rowProcessor;

    public PreviewService(
        IPipelineReadinessService readinessService,
        PipelineRowProcessor rowProcessor)
    {
        ArgumentNullException.ThrowIfNull(readinessService);
        ArgumentNullException.ThrowIfNull(rowProcessor);

        _readinessService = readinessService;
        _rowProcessor = rowProcessor;
    }

    public async Task<PreviewResult> PreviewAsync(
        IEtlSource source,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);

        cancellationToken.ThrowIfCancellationRequested();

        var readiness = _readinessService.Evaluate(pipeline);
        if (!readiness.IsReady)
        {
            throw new PipelineNotReadyException(
                readiness.Problems,
                "The pipeline is not ready for preview.");
        }

        var session = _rowProcessor.CreateSession(pipeline);
        var rows = new List<RowProcessingResult>(MaximumSourceRows);

        await foreach (var sourceRow in source
            .ReadAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processedRow = session.Process(sourceRow);
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(processedRow);

            if (rows.Count == MaximumSourceRows)
            {
                break;
            }
        }

        return new PreviewResult(rows);
    }
}
