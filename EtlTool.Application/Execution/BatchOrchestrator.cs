using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Execution;

public sealed class BatchOrchestrator : IBatchOrchestrator
{
    private readonly IFileExtractorResolver _extractorResolver;
    private readonly IPipelineReadinessService _readinessService;
    private readonly PipelineRowProcessor _rowProcessor;
    private readonly IMongoTargetAccessService _targetAccessService;
    private readonly int _batchSize;

    public BatchOrchestrator(
        IFileExtractorResolver extractorResolver,
        IPipelineReadinessService readinessService,
        PipelineRowProcessor rowProcessor,
        IMongoTargetAccessService targetAccessService,
        BatchExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(extractorResolver);
        ArgumentNullException.ThrowIfNull(readinessService);
        ArgumentNullException.ThrowIfNull(rowProcessor);
        ArgumentNullException.ThrowIfNull(targetAccessService);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _extractorResolver = extractorResolver;
        _readinessService = readinessService;
        _rowProcessor = rowProcessor;
        _targetAccessService = targetAccessService;
        _batchSize = options.BatchSize;
    }

    public Task<BatchExecutionResult> ExecuteAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task> processBatchAsync,
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processBatchAsync);

        return ExecuteWithLoadResultAsync(
            source,
            pipeline,
            async (batch, token) =>
            {
                await processBatchAsync(batch, token).ConfigureAwait(false);
                return BatchLoadResult.Empty;
            },
            reportProgressAsync,
            cancellationToken);
    }

    public Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> processBatchAsync,
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        CancellationToken cancellationToken) =>
        ExecuteWithLoadResultAsync(
            source,
            pipeline,
            processBatchAsync,
            static (_, _) => Task.CompletedTask,
            reportProgressAsync,
            cancellationToken);

    public async Task<BatchExecutionResult> ExecuteWithLoadResultAsync(
        Stream source,
        PipelineDefinition pipeline,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> processBatchAsync,
        Func<RowProcessingResult, CancellationToken, Task> reportInvalidRowAsync,
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(processBatchAsync);
        ArgumentNullException.ThrowIfNull(reportInvalidRowAsync);
        ArgumentNullException.ThrowIfNull(reportProgressAsync);

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

        var target = new MongoTarget(
            pipeline.DestinationDatabase,
            pipeline.DestinationCollection);
        await _targetAccessService.EnsureAccessibleAsync(
            target,
            cancellationToken).ConfigureAwait(false);
        await _targetAccessService.EnsureUpsertIndexAsync(
            target,
            pipeline.UpsertKeyField,
            cancellationToken).ConfigureAwait(false);

        var extractor = _extractorResolver.Resolve(pipeline.SourceType);
        var session = _rowProcessor.CreateSession(pipeline);
        var currentBatch = new List<DataRow>(_batchSize);
        long processedRows = 0;
        long validRows = 0;
        long invalidRows = 0;
        long filteredRows = 0;
        long deduplicatedRows = 0;
        long insertedRows = 0;
        long updatedRows = 0;
        long lastReportedProcessedRows = 0;

        try
        {
            await foreach (var sourceRow in extractor
                .ReadAsync(source, pipeline.SourceOptions, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = session.Process(sourceRow);
                cancellationToken.ThrowIfCancellationRequested();
                processedRows++;
                var reportedFullBatch = false;

                switch (result.Status)
                {
                    case RowProcessingStatus.Valid:
                        validRows++;
                        currentBatch.Add(result.Row);
                        if (currentBatch.Count == _batchSize)
                        {
                            var loadResult = await LoadBatchAsync(
                                currentBatch,
                                processBatchAsync,
                                processedRows,
                                validRows,
                                invalidRows,
                                filteredRows,
                                deduplicatedRows,
                                insertedRows,
                                updatedRows,
                                cancellationToken).ConfigureAwait(false);
                            insertedRows += loadResult.InsertedRows;
                            updatedRows += loadResult.UpdatedRows;
                            currentBatch = new List<DataRow>(_batchSize);
                            await ReportProgressAsync(
                                reportProgressAsync,
                                processedRows,
                                validRows,
                                invalidRows,
                                filteredRows,
                                deduplicatedRows,
                                insertedRows,
                                updatedRows,
                                isCompleted: false,
                                cancellationToken).ConfigureAwait(false);
                            lastReportedProcessedRows = processedRows;
                            reportedFullBatch = true;
                        }

                        break;
                    case RowProcessingStatus.Invalid:
                        invalidRows++;
                        await reportInvalidRowAsync(result, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
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

                if (!reportedFullBatch && processedRows - lastReportedProcessedRows >= _batchSize)
                {
                    await ReportProgressAsync(
                        reportProgressAsync,
                        processedRows,
                        validRows,
                        invalidRows,
                        filteredRows,
                        deduplicatedRows,
                        insertedRows,
                        updatedRows,
                        isCompleted: false,
                        cancellationToken).ConfigureAwait(false);
                    lastReportedProcessedRows = processedRows;
                }
            }

            if (currentBatch.Count > 0)
            {
                var loadResult = await LoadBatchAsync(
                    currentBatch,
                    processBatchAsync,
                    processedRows,
                    validRows,
                    invalidRows,
                    filteredRows,
                    deduplicatedRows,
                    insertedRows,
                    updatedRows,
                    cancellationToken).ConfigureAwait(false);
                insertedRows += loadResult.InsertedRows;
                updatedRows += loadResult.UpdatedRows;
            }

            await ReportProgressAsync(
                reportProgressAsync,
                processedRows,
                validRows,
                invalidRows,
                filteredRows,
                deduplicatedRows,
                insertedRows,
                updatedRows,
                isCompleted: true,
                cancellationToken).ConfigureAwait(false);

            return new BatchExecutionResult(
                processedRows,
                validRows,
                invalidRows,
                filteredRows,
                deduplicatedRows,
                insertedRows,
                updatedRows);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var progress = new BatchExecutionProgress(
                processedRows,
                validRows,
                invalidRows,
                filteredRows,
                deduplicatedRows,
                isCompleted: false,
                insertedRows,
                updatedRows);
            throw new BatchExecutionCanceledException(progress, exception, cancellationToken);
        }
    }

    private static async Task<BatchLoadResult> LoadBatchAsync(
        IReadOnlyList<DataRow> batch,
        Func<IReadOnlyList<DataRow>, CancellationToken, Task<BatchLoadResult>> processBatchAsync,
        long processedRows,
        long validRows,
        long invalidRows,
        long filteredRows,
        long deduplicatedRows,
        long insertedRows,
        long updatedRows,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await processBatchAsync(batch, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The batch loader returned no result.");

            if (result.InsertedRows + result.UpdatedRows > batch.Count)
            {
                throw new InvalidOperationException(
                    "The batch loader reported more committed rows than the supplied batch contains.");
            }

            return result;
        }
        catch (BatchLoadException exception)
        {
            if (exception.ConfirmedResult.InsertedRows + exception.ConfirmedResult.UpdatedRows > batch.Count)
            {
                throw new InvalidOperationException(
                    "The failed batch reported more committed rows than the supplied batch contains.",
                    exception);
            }

            var progress = new BatchExecutionProgress(
                processedRows,
                validRows,
                invalidRows,
                filteredRows,
                deduplicatedRows,
                isCompleted: false,
                insertedRows + exception.ConfirmedResult.InsertedRows,
                updatedRows + exception.ConfirmedResult.UpdatedRows);

            throw new BatchExecutionException(progress, exception);
        }
    }

    private static async Task ReportProgressAsync(
        Func<BatchExecutionProgress, CancellationToken, Task> reportProgressAsync,
        long processedRows,
        long validRows,
        long invalidRows,
        long filteredRows,
        long deduplicatedRows,
        long insertedRows,
        long updatedRows,
        bool isCompleted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var progress = new BatchExecutionProgress(
            processedRows,
            validRows,
            invalidRows,
            filteredRows,
            deduplicatedRows,
            isCompleted,
            insertedRows,
            updatedRows);
        await reportProgressAsync(progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
