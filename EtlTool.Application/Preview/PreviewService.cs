using EtlTool.Application.Extraction;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Preview;

public sealed class PreviewService : IPreviewService
{
    private const int MaximumSourceRows = 100;

    private readonly IFileExtractorResolver _extractorResolver;
    private readonly IPipelineReadinessService _readinessService;
    private readonly PipelineRowProcessor _rowProcessor;

    public PreviewService(
        IFileExtractorResolver extractorResolver,
        IPipelineReadinessService readinessService,
        PipelineRowProcessor rowProcessor)
    {
        ArgumentNullException.ThrowIfNull(extractorResolver);
        ArgumentNullException.ThrowIfNull(readinessService);
        ArgumentNullException.ThrowIfNull(rowProcessor);

        _extractorResolver = extractorResolver;
        _readinessService = readinessService;
        _rowProcessor = rowProcessor;
    }

    public async Task<PreviewResult> PreviewAsync(
        Stream source,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);

        if (!source.CanRead)
        {
            throw new ArgumentException("The preview source stream must be readable.", nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var readiness = _readinessService.Evaluate(pipeline);
        if (!readiness.IsReady)
        {
            throw new PipelineNotReadyException(readiness.Problems);
        }

        var extractor = _extractorResolver.Resolve(pipeline.SourceType);
        var session = _rowProcessor.CreateSession(pipeline);
        var rows = new List<RowProcessingResult>(MaximumSourceRows);

        await foreach (var sourceRow in extractor
            .ReadAsync(source, pipeline.SourceOptions, cancellationToken)
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
