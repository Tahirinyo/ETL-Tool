using EtlTool.Application.Extraction;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Execution;

public sealed class BatchOrchestrator : IBatchOrchestrator
{
    private readonly IFileExtractorResolver _extractorResolver;
    private readonly IPipelineReadinessService _readinessService;
    private readonly PipelineRowProcessor _rowProcessor;
    private readonly int _batchSize;

    public BatchOrchestrator(
        IFileExtractorResolver extractorResolver,
        IPipelineReadinessService readinessService,
        PipelineRowProcessor rowProcessor,
        BatchExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(extractorResolver);
        ArgumentNullException.ThrowIfNull(readinessService);
        ArgumentNullException.ThrowIfNull(rowProcessor);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _extractorResolver = extractorResolver;
        _readinessService = readinessService;
        _rowProcessor = rowProcessor;
        _batchSize = options.BatchSize;
    }

    public async Task<BatchExecutionResult> ExecuteAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task> processBatchAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(processBatchAsync);

        if (!source.CanRead)
        {
            throw new ArgumentException("The execution source stream must be readable.", nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var readiness = _readinessService.Evaluate(pipeline);
        if (!readiness.IsReady)
        {
            throw new PipelineNotReadyException(readiness.Problems);
        }

        var extractor = _extractorResolver.Resolve(pipeline.SourceType);
        var session = _rowProcessor.CreateSession(pipeline);
        var currentBatch = new List<DataRow>(_batchSize);
        long processedRows = 0;
        long validRows = 0;
        long invalidRows = 0;
        long filteredRows = 0;
        long deduplicatedRows = 0;

        await foreach (var sourceRow in extractor
            .ReadAsync(source, pipeline.SourceOptions, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = session.Process(sourceRow);
            cancellationToken.ThrowIfCancellationRequested();
            processedRows++;

            switch (result.Status)
            {
                case RowProcessingStatus.Valid:
                    validRows++;
                    currentBatch.Add(result.Row);
                    if (currentBatch.Count == _batchSize)
                    {
                        await processBatchAsync(currentBatch, cancellationToken).ConfigureAwait(false);
                        currentBatch = new List<DataRow>(_batchSize);
                    }

                    break;
                case RowProcessingStatus.Invalid:
                    invalidRows++;
                    break;
                case RowProcessingStatus.Filtered:
                    filteredRows++;
                    break;
                case RowProcessingStatus.Duplicate:
                    deduplicatedRows++;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The row-processing status '{result.Status}' is not supported.");
            }
        }

        if (currentBatch.Count > 0)
        {
            await processBatchAsync(currentBatch, cancellationToken).ConfigureAwait(false);
        }

        return new BatchExecutionResult(
            processedRows,
            validRows,
            invalidRows,
            filteredRows,
            deduplicatedRows);
    }
}
