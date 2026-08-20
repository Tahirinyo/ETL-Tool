using EtlTool.Application.Extraction;
using EtlTool.Application.Sources;
using EtlTool.Application.Uploads;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;

namespace EtlTool.Infrastructure.Sources;

public sealed class SourceInspectionService : ISourceInspectionService
{
    private const int SampleSize = 100;
    private static readonly TimeSpan StageLifetime = TimeSpan.FromMinutes(15);
    private readonly IUploadValidationService _validation;
    private readonly IUploadStorage _storage;
    private readonly CsvFileExtractor _csv;
    private readonly XlsxFileExtractor _xlsx;
    private readonly SourceSchemaInferenceService _schemaInference;
    private readonly TimeProvider _timeProvider;
    private readonly object _stageLock = new();
    private readonly Dictionary<Guid, StagedUpload> _staged = [];
    private readonly Dictionary<Guid, StagedUpload> _inspecting = [];

    public SourceInspectionService(
        IUploadValidationService validation,
        IUploadStorage storage,
        CsvFileExtractor csv,
        XlsxFileExtractor xlsx,
        TimeProvider? timeProvider = null,
        SourceSchemaInferenceService? schemaInference = null)
    {
        _validation = validation;
        _storage = storage;
        _csv = csv;
        _xlsx = xlsx;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _schemaInference = schemaInference ?? new SourceSchemaInferenceService();
    }

    public async Task<SourceInspectionResult> InspectCsvAsync(Stream content, string fileName, SourceOptions options, CancellationToken cancellationToken)
    {
        if (options.Delimiter is not null
            && options.Delimiter is not CsvDelimiter.Comma
            && options.Delimiter is not CsvDelimiter.Semicolon
            && options.Delimiter is not CsvDelimiter.Tab)
        {
            return Failure(SourceType.Csv, "Choose comma, semicolon, or tab as the CSV delimiter.");
        }
        var result = await _validation.StoreValidatedAsync(content, fileName, SourceType.Csv, options, cancellationToken);
        if (!result.IsValid) return Failure(SourceType.Csv, result.Failure!.Message);
        Exception? inspectionException = null;
        try
        {
            return await InspectAsync(result.Upload!, SourceType.Csv, options, _csv, cancellationToken);
        }
        catch (Exception exception)
        {
            inspectionException = exception;
            throw;
        }
        finally
        {
            await DeleteAfterInspectionAsync(result.Upload!, inspectionException);
        }
    }

    public async Task<SourceInspectionResult> StageXlsxAsync(
        Guid pipelineId,
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
            return Failure(SourceType.Xlsx, "The pipeline identity is invalid.");

        await PurgeExpiredAsync();
        var result = await _validation.StoreForWorksheetSelectionAsync(content, fileName, cancellationToken);
        if (!result.IsValid) return Failure(SourceType.Xlsx, result.Failure!.Message);
        var id = Guid.NewGuid();
        lock (_stageLock)
        {
            _staged[id] = new StagedUpload(
                result.Upload!,
                pipelineId,
                _timeProvider.GetUtcNow().Add(StageLifetime));
        }

        return new SourceInspectionResult { SourceType = SourceType.Xlsx, StageId = id, WorksheetNames = result.WorksheetNames };
    }

    public async Task<SourceInspectionResult> InspectStagedXlsxAsync(
        Guid pipelineId,
        Guid stageId,
        string worksheetName,
        CancellationToken cancellationToken,
        SourceOptions? sourceOptions = null)
    {
        await PurgeExpiredAsync();
        StagedUpload? staged = null;
        var belongsToDifferentPipeline = false;
        if (pipelineId != Guid.Empty && stageId != Guid.Empty)
        {
            lock (_stageLock)
            {
                if (_staged.TryGetValue(stageId, out var registered))
                {
                    if (registered.PipelineId != pipelineId)
                    {
                        belongsToDifferentPipeline = true;
                    }
                    else if (_staged.Remove(stageId))
                    {
                        _inspecting[stageId] = registered;
                        staged = registered;
                    }
                }
            }
        }

        if (staged is null)
        {
            if (belongsToDifferentPipeline)
                return Failure(SourceType.Xlsx, "The uploaded workbook was staged for a different pipeline.");

            return Failure(SourceType.Xlsx, "The uploaded workbook selection has expired. Upload the file again.");
        }
        Exception? inspectionException = null;
        SourceInspectionResult? inspectionResult = null;
        try
        {
            var options = new SourceOptions
            {
                WorksheetName = worksheetName,
                FirstRowIsHeader = true,
                CultureName = sourceOptions?.CultureName ?? string.Empty,
                DateFormat = sourceOptions?.DateFormat
            };
            var validation = await _validation.ValidateStoredAsync(staged.Upload, SourceType.Xlsx, options, cancellationToken);
            inspectionResult = !validation.IsValid
                ? Failure(SourceType.Xlsx, validation.Failure!.Message)
                : await InspectAsync(staged.Upload, SourceType.Xlsx, options, _xlsx, cancellationToken);
            return inspectionResult;
        }
        catch (Exception exception)
        {
            inspectionException = exception;
            throw;
        }
        finally
        {
            lock (_stageLock)
            {
                _inspecting.Remove(stageId);
            }

            await DeleteAfterInspectionAsync(
                staged.Upload,
                inspectionException,
                inspectionResult is { IsSuccess: false });
        }
    }

    private async Task<SourceInspectionResult> InspectAsync(StoredUpload upload, SourceType sourceType, SourceOptions options, IFileExtractor extractor, CancellationToken ct)
    {
        await using var headersStream = File.OpenRead(upload.StoredFilePath);
        var columns = await extractor.ReadHeadersAsync(headersStream, options, ct);
        await using var sampleStream = File.OpenRead(upload.StoredFilePath);
        var rows = new List<DataRow>();
        await foreach (var row in extractor.ReadAsync(sampleStream, options, ct))
        {
            rows.Add(row);
            if (rows.Count == SampleSize) break;
        }
        var schema = _schemaInference.Infer(columns, rows, options, ct);
        return new SourceInspectionResult
        {
            SourceType = sourceType,
            Columns = columns,
            SampleRows = rows,
            DetectedSchema = schema
        };
    }

    public async Task PurgeExpiredAsync()
    {
        var now = _timeProvider.GetUtcNow();
        List<Exception>? cleanupFailures = null;

        while (TryTakeExpiredStage(now, out var expired))
        {
            try
            {
                await _storage.DeleteAsync(expired.Upload, CancellationToken.None);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupFailures ??= [];
                cleanupFailures.Add(exception);
            }
        }

        if (cleanupFailures is { Count: > 0 })
        {
            throw new IOException(
                "One or more expired staged uploads could not be removed.",
                new AggregateException(cleanupFailures));
        }
    }

    public Task PurgeOrphanedUploadsAsync(CancellationToken cancellationToken)
    {
        return _storage.DeleteExpiredAsync(
            _timeProvider.GetUtcNow().Subtract(StageLifetime),
            cancellationToken);
    }

    private bool TryTakeExpiredStage(DateTimeOffset now, out StagedUpload expired)
    {
        lock (_stageLock)
        {
            Guid? expiredStageId = null;

            foreach (var pair in _staged)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    expiredStageId = pair.Key;
                    break;
                }
            }

            if (expiredStageId is Guid stageId
                && _staged.Remove(stageId, out var stage))
            {
                expired = stage;
                return true;
            }
        }

        expired = null!;
        return false;
    }

    private async Task DeleteAfterInspectionAsync(
        StoredUpload upload,
        Exception? inspectionException,
        bool preserveFailureResult = false)
    {
        try
        {
            await _storage.DeleteAsync(upload, CancellationToken.None);
        }
        catch (Exception cleanupException)
            when ((inspectionException is not null || preserveFailureResult)
                && cleanupException is IOException or UnauthorizedAccessException)
        {
            // Storage ownership is already released, so orphan cleanup can retry without
            // replacing the inspection's authoritative failure or cancellation.
            if (inspectionException is not null)
            {
                inspectionException.Data["EtlTool.UploadCleanupFailure"] = cleanupException;
            }
        }
    }

    private static SourceInspectionResult Failure(SourceType type, string message) => new() { SourceType = type, ErrorMessage = message };
    private sealed record StagedUpload(
        StoredUpload Upload,
        Guid PipelineId,
        DateTimeOffset ExpiresAt);
}
