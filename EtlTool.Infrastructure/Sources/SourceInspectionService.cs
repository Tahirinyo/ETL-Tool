using EtlTool.Application.Extraction;
using EtlTool.Application.Sources;
using EtlTool.Application.Uploads;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using Microsoft.Extensions.Logging;

namespace EtlTool.Infrastructure.Sources;

public sealed class SourceInspectionService : ISourceInspectionService, IWizardSourceStore
{
    private const int SampleSize = 100;
    private static readonly TimeSpan StageLifetime = TimeSpan.FromMinutes(15);
    private readonly IUploadValidationService _validation;
    private readonly IUploadStorage _storage;
    private readonly CsvFileExtractor _csv;
    private readonly XlsxFileExtractor _xlsx;
    private readonly SourceSchemaInferenceService _schemaInference;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SourceInspectionService>? _logger;
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, StagedUpload> _staged = [];
    private readonly Dictionary<Guid, StagedUpload> _inspecting = [];
    private readonly Dictionary<Guid, PendingSource> _pending = [];
    private readonly Dictionary<Guid, ActiveSource> _active = [];

    public SourceInspectionService(
        IUploadValidationService validation,
        IUploadStorage storage,
        CsvFileExtractor csv,
        XlsxFileExtractor xlsx,
        TimeProvider? timeProvider = null,
        SourceSchemaInferenceService? schemaInference = null,
        ILogger<SourceInspectionService>? logger = null)
    {
        _validation = validation;
        _storage = storage;
        _csv = csv;
        _xlsx = xlsx;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _schemaInference = schemaInference ?? new SourceSchemaInferenceService();
        _logger = logger;
    }

    public async Task<SourceInspectionResult> InspectCsvAsync(
        Guid pipelineId,
        Stream content,
        string fileName,
        SourceOptions options,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            return Failure(SourceType.Csv, "The pipeline identity is invalid.");
        }

        if (options.Delimiter is not null
            && options.Delimiter is not CsvDelimiter.Comma
            && options.Delimiter is not CsvDelimiter.Semicolon
            && options.Delimiter is not CsvDelimiter.Tab)
        {
            return Failure(SourceType.Csv, "Choose comma, semicolon, or tab as the CSV delimiter.");
        }

        await PurgeExpiredAsync();
        var validation = await _validation.StoreValidatedAsync(
            content,
            fileName,
            SourceType.Csv,
            options,
            cancellationToken);
        if (!validation.IsValid)
        {
            return Failure(SourceType.Csv, validation.Failure!.Message);
        }

        var upload = validation.Upload!;
        var sourceReferenceId = Guid.NewGuid();
        var retained = false;
        Exception? inspectionException = null;

        try
        {
            var inspection = await InspectAsync(
                upload,
                SourceType.Csv,
                options,
                _csv,
                cancellationToken,
                sourceReferenceId);

            var replacedPending = false;
            lock (_stateLock)
            {
                replacedPending = _pending.ContainsKey(sourceReferenceId);
                _pending[sourceReferenceId] = new PendingSource(
                    upload,
                    pipelineId,
                    SourceType.Csv,
                    CopyOptions(options),
                    CopySchema(inspection.DetectedSchema),
                    _timeProvider.GetUtcNow().Add(StageLifetime));
            }

            LogLifecycle(
                "PendingCreated",
                pipelineId,
                sourceReferenceId,
                SourceType.Csv,
                replacedPending ? "Replaced" : "Created");

            retained = true;
            return inspection;
        }
        catch (Exception exception)
        {
            inspectionException = exception;
            throw;
        }
        finally
        {
            if (!retained)
            {
                await DeleteAfterInspectionAsync(upload, inspectionException);
            }
        }
    }

    public async Task<SourceInspectionResult> StageXlsxAsync(
        Guid pipelineId,
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            return Failure(SourceType.Xlsx, "The pipeline identity is invalid.");
        }

        await PurgeExpiredAsync();
        var result = await _validation.StoreForWorksheetSelectionAsync(
            content,
            fileName,
            cancellationToken);
        if (!result.IsValid)
        {
            return Failure(SourceType.Xlsx, result.Failure!.Message);
        }

        var id = Guid.NewGuid();
        lock (_stateLock)
        {
            _staged[id] = new StagedUpload(
                result.Upload!,
                pipelineId,
                _timeProvider.GetUtcNow().Add(StageLifetime));
        }

        return new SourceInspectionResult
        {
            SourceType = SourceType.Xlsx,
            StageId = id,
            WorksheetNames = result.WorksheetNames
        };
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
            lock (_stateLock)
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
            return belongsToDifferentPipeline
                ? Failure(SourceType.Xlsx, "The uploaded workbook was staged for a different pipeline.")
                : Failure(SourceType.Xlsx, "The uploaded workbook selection has expired. Upload the file again.");
        }

        Exception? inspectionException = null;
        SourceInspectionResult? inspectionResult = null;
        var retained = false;

        try
        {
            var options = new SourceOptions
            {
                WorksheetName = worksheetName,
                FirstRowIsHeader = true,
                CultureName = sourceOptions?.CultureName ?? string.Empty,
                DateFormat = sourceOptions?.DateFormat
            };
            var validation = await _validation.ValidateStoredAsync(
                staged.Upload,
                SourceType.Xlsx,
                options,
                cancellationToken);
            inspectionResult = !validation.IsValid
                ? Failure(SourceType.Xlsx, validation.Failure!.Message)
                : await InspectAsync(
                    staged.Upload,
                    SourceType.Xlsx,
                    options,
                    _xlsx,
                    cancellationToken,
                    stageId);

            if (inspectionResult.IsSuccess)
            {
                var replacedPending = false;
                lock (_stateLock)
                {
                    replacedPending = _pending.ContainsKey(stageId);
                    _pending[stageId] = new PendingSource(
                        staged.Upload,
                        pipelineId,
                        SourceType.Xlsx,
                        CopyOptions(options),
                        CopySchema(inspectionResult.DetectedSchema),
                        _timeProvider.GetUtcNow().Add(StageLifetime));
                }

                LogLifecycle(
                    "PendingCreated",
                    pipelineId,
                    stageId,
                    SourceType.Xlsx,
                    replacedPending ? "Replaced" : "Created");

                retained = true;
            }

            return inspectionResult;
        }
        catch (Exception exception)
        {
            inspectionException = exception;
            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                _inspecting.Remove(stageId);
            }

            if (!retained)
            {
                await DeleteAfterInspectionAsync(
                    staged.Upload,
                    inspectionException,
                    inspectionResult is { IsSuccess: false });
            }
        }
    }

    public async Task<bool> ActivateAsync(
        Guid pipelineId,
        Guid sourceReferenceId,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty || sourceReferenceId == Guid.Empty)
        {
            return false;
        }

        await PurgeExpiredAsync();
        cancellationToken.ThrowIfCancellationRequested();
        ActiveSource? retired = null;
        ActiveSource? activated = null;

        lock (_stateLock)
        {
            if (!_pending.TryGetValue(sourceReferenceId, out var pending)
                || pending.PipelineId != pipelineId)
            {
                return false;
            }

            if (_active.TryGetValue(pipelineId, out var reserved)
                && reserved.IsRunReserved)
            {
                return false;
            }

            _pending.Remove(sourceReferenceId);
            if (_active.Remove(pipelineId, out var previous))
            {
                previous.IsRetired = true;
                if (previous.LeaseCount == 0)
                {
                    retired = previous;
                }
            }

            _active[pipelineId] = new ActiveSource(
                pending.Upload,
                sourceReferenceId,
                pending.SourceType,
                pending.Options,
                pending.ExpiresAt);
            activated = _active[pipelineId];
        }

        LogLifecycle(
            "Activated",
            pipelineId,
            sourceReferenceId,
            activated!.SourceType,
            "PendingPromoted");

        if (retired is not null)
        {
            LogLifecycle(
                "Retired",
                pipelineId,
                retired.SourceReferenceId,
                retired.SourceType,
                "Replaced");
            try
            {
                await DeleteUploadsAsync([retired.Upload]);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // DeleteAsync releases ownership before physical deletion. A failed deletion is
                // therefore an orphan-cleanup concern, not a failure of the completed source swap.
            }
        }

        return true;
    }

    public async Task<PendingSourceInspection?> GetPendingSourceAsync(
        Guid pipelineId,
        Guid sourceReferenceId,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty || sourceReferenceId == Guid.Empty)
        {
            return null;
        }

        await PurgeExpiredAsync();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (!_pending.TryGetValue(sourceReferenceId, out var pending)
                || pending.PipelineId != pipelineId)
            {
                return null;
            }

            return new PendingSourceInspection
            {
                SourceType = pending.SourceType,
                SourceOptions = CopyOptions(pending.Options),
                DetectedSchema = CopySchema(pending.Schema)
            };
        }
    }

    public async Task DiscardAsync(
        Guid sourceReferenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PendingSource? pending;

        lock (_stateLock)
        {
            _pending.Remove(sourceReferenceId, out pending);
        }

        if (pending is not null)
        {
            LogLifecycle(
                "PendingDiscarded",
                pending.PipelineId,
                sourceReferenceId,
                pending.SourceType,
                "Discarded");
            await DeleteUploadsAsync([pending.Upload]);
        }
    }

    public async Task<IWizardSourceLease?> AcquireAsync(
        Guid pipelineId,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceOptions);
        if (pipelineId == Guid.Empty)
        {
            return null;
        }

        var activeWasExpired = IsActiveSourceExpired(pipelineId);
        await PurgeExpiredAsync();
        cancellationToken.ThrowIfCancellationRequested();
        ActiveSource? active = null;
        Guid? sourceReferenceId = null;
        var failureReason = SourceAcquisitionFailureReason.None;

        lock (_stateLock)
        {
            if (!_active.TryGetValue(pipelineId, out active))
            {
                failureReason = activeWasExpired
                    ? SourceAcquisitionFailureReason.Expired
                    : SourceAcquisitionFailureReason.NoActiveSource;
            }
            else if (active.IsRetired)
            {
                sourceReferenceId = active.SourceReferenceId;
                failureReason = SourceAcquisitionFailureReason.Retired;
            }
            else if (active.IsRunReserved)
            {
                sourceReferenceId = active.SourceReferenceId;
                failureReason = SourceAcquisitionFailureReason.RunReserved;
            }
            else if (active.SourceType != sourceType)
            {
                sourceReferenceId = active.SourceReferenceId;
                failureReason = SourceAcquisitionFailureReason.SourceTypeMismatch;
            }
            else if (!OptionsMatch(active.Options, sourceOptions))
            {
                sourceReferenceId = active.SourceReferenceId;
                failureReason = SourceAcquisitionFailureReason.SourceOptionsMismatch;
            }
            else
            {
                active.LeaseCount++;
                sourceReferenceId = active.SourceReferenceId;
            }
        }

        if (failureReason != SourceAcquisitionFailureReason.None)
        {
            LogAcquisition(pipelineId, sourceType, false, failureReason, sourceReferenceId, fileExists: null);
            return null;
        }

        var acquiredActive = active!;
        try
        {
            IFileExtractor extractor = acquiredActive.SourceType switch
            {
                SourceType.Csv => _csv,
                SourceType.Xlsx => _xlsx,
                _ => throw new InvalidOperationException(
                    $"The active source type '{acquiredActive.SourceType}' is not supported.")
            };
            var stream = new FileStream(
                acquiredActive.Upload.StoredFilePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 81_920,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            LogAcquisition(pipelineId, sourceType, true, SourceAcquisitionFailureReason.None,
                sourceReferenceId, fileExists: true);
            return new WizardSourceLease(
                this,
                acquiredActive,
                new FileEtlSource(stream, extractor, acquiredActive.Options));
        }
        catch (FileNotFoundException)
        {
            LogAcquisition(pipelineId, sourceType, false, SourceAcquisitionFailureReason.PhysicalSourceUnavailable,
                sourceReferenceId, fileExists: false);
            await RetireMissingSourceAsync(pipelineId, acquiredActive);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            LogAcquisition(pipelineId, sourceType, false, SourceAcquisitionFailureReason.PhysicalSourceUnavailable,
                sourceReferenceId, fileExists: false);
            await RetireMissingSourceAsync(pipelineId, acquiredActive);
            return null;
        }
        catch
        {
            await ReleaseAsync(acquiredActive);
            throw;
        }
    }

    public async Task<IWizardRunSourceReservation?> ReserveForRunAsync(
        Guid pipelineId,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceOptions);
        if (pipelineId == Guid.Empty)
        {
            return null;
        }

        await PurgeExpiredAsync();
        cancellationToken.ThrowIfCancellationRequested();
        ActiveSource? active;

        lock (_stateLock)
        {
            if (!_active.TryGetValue(pipelineId, out active)
                || active.IsRetired
                || active.IsRunReserved
                || active.SourceType != sourceType
                || !OptionsMatch(active.Options, sourceOptions))
            {
                return null;
            }

            active.IsRunReserved = true;
            active.LeaseCount++;
        }

        LogLifecycle(
            "ReservedForRun",
            pipelineId,
            active.SourceReferenceId,
            active.SourceType,
            "Reserved");

        return new WizardRunSourceReservation(this, pipelineId, active);
    }

    public async Task RemoveAsync(
        Guid pipelineId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveSource? removed = null;
        ActiveSource? retired = null;
        var uploads = new List<StoredUpload>();

        lock (_stateLock)
        {
            foreach (var stageId in _staged
                .Where(pair => pair.Value.PipelineId == pipelineId)
                .Select(pair => pair.Key)
                .ToArray())
            {
                uploads.Add(_staged[stageId].Upload);
                _staged.Remove(stageId);
            }

            foreach (var sourceReferenceId in _pending
                .Where(pair => pair.Value.PipelineId == pipelineId)
                .Select(pair => pair.Key)
                .ToArray())
            {
                uploads.Add(_pending[sourceReferenceId].Upload);
                _pending.Remove(sourceReferenceId);
            }

            if (_active.Remove(pipelineId, out var active))
            {
                active.IsRetired = true;
                retired = active;
                if (active.LeaseCount == 0)
                {
                    removed = active;
                }
            }
        }

        if (retired is not null)
        {
            LogLifecycle(
                "Retired",
                pipelineId,
                retired.SourceReferenceId,
                retired.SourceType,
                "PipelineRemoved");
        }

        if (removed is not null)
        {
            uploads.Add(removed.Upload);
        }

        await DeleteUploadsAsync(uploads);
    }

    public async Task RetireActiveAsync(
        Guid pipelineId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveSource? retired = null;
        ActiveSource? retiredForCleanup = null;

        lock (_stateLock)
        {
            if (_active.Remove(pipelineId, out var active))
            {
                active.IsRetired = true;
                retired = active;
                if (active.LeaseCount == 0)
                {
                    retiredForCleanup = active;
                }
            }
        }

        if (retired is not null)
        {
            LogLifecycle(
                "Retired",
                pipelineId,
                retired.SourceReferenceId,
                retired.SourceType,
                "ExplicitRetirement");
        }

        if (retiredForCleanup is not null)
        {
            await DeleteUploadsAsync([retiredForCleanup.Upload]);
        }
    }

    private async Task<SourceInspectionResult> InspectAsync(
        StoredUpload upload,
        SourceType sourceType,
        SourceOptions options,
        IFileExtractor extractor,
        CancellationToken cancellationToken,
        Guid sourceReferenceId)
    {
        await using var headersStream = File.OpenRead(upload.StoredFilePath);
        var columns = await extractor.ReadHeadersAsync(headersStream, options, cancellationToken);
        await using var sampleStream = File.OpenRead(upload.StoredFilePath);
        var rows = new List<DataRow>();
        await foreach (var row in extractor.ReadAsync(sampleStream, options, cancellationToken))
        {
            rows.Add(row);
            if (rows.Count == SampleSize)
            {
                break;
            }
        }

        var schema = _schemaInference.Infer(columns, rows, options, cancellationToken);
        return new SourceInspectionResult
        {
            SourceType = sourceType,
            Columns = columns,
            SampleRows = rows,
            DetectedSchema = schema,
            SourceReferenceId = sourceReferenceId
        };
    }

    public async Task PurgeExpiredAsync()
    {
        var now = _timeProvider.GetUtcNow();
        var uploads = new List<StoredUpload>();
        var expired = new List<(Guid PipelineId, Guid SourceReferenceId, SourceType SourceType, string State)>();

        lock (_stateLock)
        {
            foreach (var stageId in _staged
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .ToArray())
            {
                uploads.Add(_staged[stageId].Upload);
                _staged.Remove(stageId);
                expired.Add((Guid.Empty, stageId, SourceType.Xlsx, "Staged"));
            }

            foreach (var sourceReferenceId in _pending
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .ToArray())
            {
                var pending = _pending[sourceReferenceId];
                uploads.Add(pending.Upload);
                _pending.Remove(sourceReferenceId);
                expired.Add((pending.PipelineId, sourceReferenceId, pending.SourceType, "Pending"));
            }

            foreach (var pipelineId in _active
                .Where(pair => !pair.Value.IsRunReserved && pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .ToArray())
            {
                var active = _active[pipelineId];
                _active.Remove(pipelineId);
                active.IsRetired = true;
                expired.Add((pipelineId, active.SourceReferenceId, active.SourceType, "Active"));
                if (active.LeaseCount == 0)
                {
                    uploads.Add(active.Upload);
                }
            }
        }

        foreach (var entry in expired)
        {
            LogLifecycle(
                "Expired",
                entry.PipelineId,
                entry.SourceReferenceId,
                entry.SourceType,
                entry.State);
        }

        await DeleteUploadsAsync(uploads);
    }

    public Task PurgeOrphanedUploadsAsync(CancellationToken cancellationToken) =>
        _storage.DeleteExpiredAsync(
            _timeProvider.GetUtcNow().Subtract(StageLifetime),
            cancellationToken);

    private async Task RetireMissingSourceAsync(Guid pipelineId, ActiveSource active)
    {
        lock (_stateLock)
        {
            if (_active.TryGetValue(pipelineId, out var registered)
                && ReferenceEquals(registered, active))
            {
                _active.Remove(pipelineId);
                active.IsRetired = true;
            }
        }

        LogLifecycle(
            "Retired",
            pipelineId,
            active.SourceReferenceId,
            active.SourceType,
            "PhysicalSourceUnavailable");

        await ReleaseAsync(active);
    }

    private async ValueTask ReleaseAsync(
        ActiveSource active,
        bool deletePhysicalFile = true)
    {
        StoredUpload? upload = null;

        lock (_stateLock)
        {
            if (active.LeaseCount > 0)
            {
                active.LeaseCount--;
            }

            if (deletePhysicalFile && active.IsRetired && active.LeaseCount == 0)
            {
                upload = active.Upload;
            }
        }

        if (upload is not null)
        {
            await DeleteUploadsAsync([upload]);
        }
    }

    private void TransferRunReservation(Guid pipelineId, ActiveSource active)
    {
        var transferred = false;
        lock (_stateLock)
        {
            if (!active.IsRunReserved)
            {
                return;
            }

            active.IsRunReserved = false;
            if (active.LeaseCount > 0)
            {
                active.LeaseCount--;
            }

            if (_active.TryGetValue(pipelineId, out var registered)
                && ReferenceEquals(registered, active))
            {
                _active.Remove(pipelineId);
                active.IsRetired = true;
            }

            transferred = true;
        }

        if (transferred)
        {
            LogLifecycle(
                "RunReservationReleased",
                pipelineId,
                active.SourceReferenceId,
                active.SourceType,
                "TransferredToRun");
        }
    }

    private async ValueTask RollBackRunReservationAsync(
        Guid pipelineId,
        ActiveSource active)
    {
        StoredUpload? upload = null;

        lock (_stateLock)
        {
            if (!active.IsRunReserved)
            {
                return;
            }

            active.IsRunReserved = false;
            if (active.LeaseCount > 0)
            {
                active.LeaseCount--;
            }

            if (_active.TryGetValue(pipelineId, out var registered)
                && ReferenceEquals(registered, active)
                && !active.IsRetired)
            {
                active.ExpiresAt = _timeProvider.GetUtcNow().Add(StageLifetime);
            }
            else if (active.IsRetired && active.LeaseCount == 0)
            {
                upload = active.Upload;
            }
        }

        if (upload is not null)
        {
            await DeleteUploadsAsync([upload]);
        }

        LogLifecycle(
            "RunReservationReleased",
            pipelineId,
            active.SourceReferenceId,
            active.SourceType,
            "RolledBack");
    }

    private async Task DeleteUploadsAsync(IEnumerable<StoredUpload> uploads)
    {
        List<Exception>? cleanupFailures = null;

        foreach (var upload in uploads)
        {
            try
            {
                await _storage.DeleteAsync(upload, CancellationToken.None);
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
                "One or more temporary wizard sources could not be removed.",
                new AggregateException(cleanupFailures));
        }
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
            if (inspectionException is not null)
            {
                inspectionException.Data["EtlTool.UploadCleanupFailure"] = cleanupException;
            }
        }
    }

    private bool IsActiveSourceExpired(Guid pipelineId)
    {
        lock (_stateLock)
        {
            return _active.TryGetValue(pipelineId, out var active)
                && !active.IsRunReserved
                && active.ExpiresAt <= _timeProvider.GetUtcNow();
        }
    }

    private void LogAcquisition(
        Guid pipelineId,
        SourceType requestedSourceType,
        bool succeeded,
        SourceAcquisitionFailureReason failureReason,
        Guid? sourceReferenceId,
        bool? fileExists)
    {
        _logger?.LogInformation(
            "Wizard source acquisition for pipeline {PipelineId} requested {RequestedSourceType} " +
            "completed with {AcquisitionResult}; reason {AcquisitionFailureReason}; " +
            "source reference {SourceReferenceId}; file exists {FileExists}; process {ProcessId}.",
            pipelineId,
            requestedSourceType,
            succeeded ? "Acquired" : "Unavailable",
            failureReason,
            sourceReferenceId,
            fileExists,
            Environment.ProcessId);
    }

    private void LogLifecycle(
        string transition,
        Guid pipelineId,
        Guid sourceReferenceId,
        SourceType sourceType,
        string state)
    {
        _logger?.LogInformation(
            "Wizard source lifecycle {SourceLifecycleTransition} for pipeline {PipelineId}; " +
            "source reference {SourceReferenceId}; source type {SourceType}; state {SourceLifecycleState}; " +
            "process {ProcessId}.",
            transition,
            pipelineId,
            sourceReferenceId,
            sourceType,
            state,
            Environment.ProcessId);
    }

    private static SourceOptions CopyOptions(SourceOptions options) => new()
    {
        CultureName = options.CultureName,
        DateFormat = options.DateFormat,
        Delimiter = options.Delimiter,
        WorksheetName = options.WorksheetName,
        FirstRowIsHeader = options.FirstRowIsHeader
    };

    private static IReadOnlyList<SourceFieldDefinition> CopySchema(
        IReadOnlyList<SourceFieldDefinition> schema) => schema
        .Select(field => new SourceFieldDefinition
        {
            Name = field.Name,
            DataType = field.DataType
        })
        .ToArray();

    private static bool OptionsMatch(SourceOptions retained, SourceOptions current) =>
        string.Equals(retained.CultureName, current.CultureName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(retained.DateFormat, current.DateFormat, StringComparison.Ordinal)
        && retained.Delimiter == current.Delimiter
        && string.Equals(retained.WorksheetName, current.WorksheetName, StringComparison.OrdinalIgnoreCase)
        && retained.FirstRowIsHeader == current.FirstRowIsHeader;

    private static SourceInspectionResult Failure(SourceType type, string message) => new()
    {
        SourceType = type,
        ErrorMessage = message
    };

    private sealed record StagedUpload(
        StoredUpload Upload,
        Guid PipelineId,
        DateTimeOffset ExpiresAt);

    private sealed record PendingSource(
        StoredUpload Upload,
        Guid PipelineId,
        SourceType SourceType,
        SourceOptions Options,
        IReadOnlyList<SourceFieldDefinition> Schema,
        DateTimeOffset ExpiresAt);

    private sealed class ActiveSource(
        StoredUpload upload,
        Guid sourceReferenceId,
        SourceType sourceType,
        SourceOptions options,
        DateTimeOffset expiresAt)
    {
        public StoredUpload Upload { get; } = upload;
        public Guid SourceReferenceId { get; } = sourceReferenceId;
        public SourceType SourceType { get; } = sourceType;
        public SourceOptions Options { get; } = options;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public int LeaseCount { get; set; }
        public bool IsRetired { get; set; }
        public bool IsRunReserved { get; set; }
    }

    private enum SourceAcquisitionFailureReason
    {
        None,
        NoActiveSource,
        Retired,
        RunReserved,
        SourceTypeMismatch,
        SourceOptionsMismatch,
        Expired,
        PhysicalSourceUnavailable
    }

    private sealed class WizardSourceLease(
        SourceInspectionService owner,
        ActiveSource active,
        IEtlSource source) : IWizardSourceLease
    {
        private int _disposed;

        public IAsyncEnumerable<DataRow> ReadAsync(CancellationToken cancellationToken) =>
            source.ReadAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await source.DisposeAsync();
            }
            finally
            {
                await owner.ReleaseAsync(active);
            }
        }
    }

    private sealed class WizardRunSourceReservation(
        SourceInspectionService owner,
        Guid pipelineId,
        ActiveSource active) : IWizardRunSourceReservation
    {
        private int _completed;

        public string OriginalFileName { get; } = active.Upload.OriginalFileName;

        public string StoredFilePath { get; } = active.Upload.StoredFilePath;

        public void TransferToRun()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                owner.TransferRunReservation(pipelineId, active);
            }
        }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _completed, 1) == 0
                ? owner.RollBackRunReservationAsync(pipelineId, active)
                : ValueTask.CompletedTask;
    }
}
