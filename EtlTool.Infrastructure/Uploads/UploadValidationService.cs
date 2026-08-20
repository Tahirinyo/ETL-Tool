using EtlTool.Application.Extraction;
using EtlTool.Application.Uploads;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;

namespace EtlTool.Infrastructure.Uploads;

public sealed class UploadValidationService : IUploadValidationService
{
    private readonly IUploadStorage _uploadStorage;
    private readonly UploadValidationOptions _options;
    private readonly CsvFileExtractor _csvExtractor;
    private readonly XlsxFileExtractor _xlsxExtractor;

    public UploadValidationService(
        IUploadStorage uploadStorage,
        UploadValidationOptions options,
        CsvFileExtractor csvExtractor,
        XlsxFileExtractor xlsxExtractor)
    {
        ArgumentNullException.ThrowIfNull(uploadStorage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(csvExtractor);
        ArgumentNullException.ThrowIfNull(xlsxExtractor);

        options.Validate();

        _uploadStorage = uploadStorage;
        _options = options;
        _csvExtractor = csvExtractor;
        _xlsxExtractor = xlsxExtractor;
    }

    public async Task<UploadValidationResult> StoreValidatedAsync(
        Stream content,
        string originalFileName,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(originalFileName);
        ArgumentNullException.ThrowIfNull(sourceOptions);

        if (!content.CanRead)
        {
            throw new ArgumentException("The uploaded content stream must be readable.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string leafFileName;

        try
        {
            leafFileName = UploadFileName.GetLeafName(originalFileName);
        }
        catch (ArgumentException)
        {
            return Rejected(
                UploadValidationFailureCode.InvalidFileName,
                "The original filename must contain a non-empty leaf filename.");
        }

        var extensionSourceType = GetExtensionSourceType(leafFileName);

        if (extensionSourceType is null)
        {
            return Rejected(
                UploadValidationFailureCode.UnsupportedExtension,
                "Only .csv and .xlsx source files are supported.");
        }

        if (sourceType is not SourceType.Csv and not SourceType.Xlsx)
        {
            return Rejected(
                UploadValidationFailureCode.UnsupportedSourceType,
                "A supported source type must be selected.");
        }

        if (sourceType != extensionSourceType)
        {
            return Rejected(
                UploadValidationFailureCode.SourceTypeMismatch,
                "The selected source type does not match the file extension.");
        }

        if (sourceType == SourceType.Xlsx
            && string.IsNullOrWhiteSpace(sourceOptions.WorksheetName))
        {
            return Rejected(
                UploadValidationFailureCode.WorksheetRequired,
                "A worksheet must be selected for an XLSX source file.");
        }

        var knownLength = TryGetRemainingLength(content);

        if (knownLength == 0)
        {
            return Rejected(
                UploadValidationFailureCode.EmptyFile,
                "The source file cannot be empty.");
        }

        if (knownLength > _options.MaxFileSizeBytes)
        {
            return FileTooLargeResult();
        }

        StoredUpload storedUpload;

        using (var limitedContent = new SizeLimitedReadStream(
            content,
            _options.MaxFileSizeBytes))
        {
            try
            {
                storedUpload = await _uploadStorage.StoreAsync(
                        limitedContent,
                        leafFileName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UploadSizeLimitExceededException)
            {
                return FileTooLargeResult();
            }
        }

        UploadValidationFailure? failure = null;
        Exception? expectedFailureException = null;

        try
        {
            if (storedUpload.SizeInBytes == 0)
            {
                failure = CreateFailure(
                    UploadValidationFailureCode.EmptyFile,
                    "The source file cannot be empty.");
            }
            else if (storedUpload.SizeInBytes > _options.MaxFileSizeBytes)
            {
                failure = CreateFileTooLargeFailure();
            }
            else
            {
                failure = await ValidateRowsAsync(
                        storedUpload,
                        sourceType,
                        sourceOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            expectedFailureException = exception;
            failure = CreateFailure(
                UploadValidationFailureCode.InvalidSourceFile,
                "The source file is malformed or cannot be read with the selected source options.");
        }
        catch (Exception validationException)
        {
            await DeleteAfterFailureAsync(storedUpload, validationException).ConfigureAwait(false);
            throw;
        }

        if (failure is not null)
        {
            await DeleteAfterFailureAsync(
                    storedUpload,
                    expectedFailureException ?? new InvalidDataException(failure.Message))
                .ConfigureAwait(false);
            return UploadValidationResult.Rejected(failure);
        }

        return UploadValidationResult.Accepted(storedUpload);
    }

    public async Task<XlsxWorksheetStageResult> StoreForWorksheetSelectionAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(originalFileName);
        string leafFileName;
        try { leafFileName = UploadFileName.GetLeafName(originalFileName); }
        catch (ArgumentException)
        {
            return XlsxWorksheetStageResult.Rejected(CreateFailure(UploadValidationFailureCode.InvalidFileName, "The original filename must contain a non-empty leaf filename."));
        }
        if (!string.Equals(Path.GetExtension(leafFileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return XlsxWorksheetStageResult.Rejected(CreateFailure(UploadValidationFailureCode.UnsupportedExtension, "Only .xlsx source files can be staged for worksheet selection."));
        }
        if (TryGetRemainingLength(content) is long length && (length == 0 || length > _options.MaxFileSizeBytes))
        {
            return XlsxWorksheetStageResult.Rejected(length == 0
                ? CreateFailure(UploadValidationFailureCode.EmptyFile, "The source file cannot be empty.")
                : CreateFileTooLargeFailure());
        }

        StoredUpload? upload = null;
        try
        {
            using var limited = new SizeLimitedReadStream(content, _options.MaxFileSizeBytes);
            upload = await _uploadStorage.StoreAsync(limited, leafFileName, cancellationToken).ConfigureAwait(false);
            await using var stream = new FileStream(upload.StoredFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var worksheets = await _xlsxExtractor.GetWorksheetNamesAsync(stream, cancellationToken).ConfigureAwait(false);
            if (worksheets.Count == 0)
            {
                throw new InvalidDataException("The XLSX workbook contains no worksheets.");
            }
            return XlsxWorksheetStageResult.Accepted(upload, worksheets);
        }
        catch (UploadSizeLimitExceededException exception)
        {
            if (upload is not null)
            {
                await DeleteAfterFailureAsync(upload, exception).ConfigureAwait(false);
            }

            return XlsxWorksheetStageResult.Rejected(CreateFileTooLargeFailure());
        }
        catch (InvalidDataException exception)
        {
            if (upload is not null)
            {
                await DeleteAfterFailureAsync(upload, exception).ConfigureAwait(false);
            }

            return XlsxWorksheetStageResult.Rejected(CreateFailure(UploadValidationFailureCode.InvalidSourceFile, "The source file is malformed or cannot be read."));
        }
        catch (Exception exception)
        {
            if (upload is not null)
            {
                await DeleteAfterFailureAsync(upload, exception).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<UploadValidationResult> ValidateStoredAsync(
        StoredUpload upload,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(sourceOptions);
        try
        {
            var failure = await ValidateRowsAsync(upload, sourceType, sourceOptions, cancellationToken).ConfigureAwait(false);
            return failure is null ? UploadValidationResult.Accepted(upload) : UploadValidationResult.Rejected(failure);
        }
        catch (InvalidDataException)
        {
            return UploadValidationResult.Rejected(CreateFailure(UploadValidationFailureCode.InvalidSourceFile, "The source file is malformed or cannot be read with the selected source options."));
        }
    }

    private async Task<UploadValidationFailure?> ValidateRowsAsync(
        StoredUpload storedUpload,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            storedUpload.StoredFilePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81_920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

        var extractor = GetExtractor(sourceType);
        var rowCount = 0;

        await foreach (var _ in extractor.ReadAsync(
            stream,
            sourceOptions,
            cancellationToken))
        {
            rowCount++;

            if (rowCount > _options.MaxDataRowCount)
            {
                return CreateFailure(
                    UploadValidationFailureCode.RowLimitExceeded,
                    $"The source file contains more than {_options.MaxDataRowCount} data rows.");
            }
        }

        return null;
    }

    private IFileExtractor GetExtractor(SourceType sourceType)
    {
        return sourceType switch
        {
            SourceType.Csv => _csvExtractor,
            SourceType.Xlsx => _xlsxExtractor,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceType),
                sourceType,
                "The source type is not supported.")
        };
    }

    private async Task DeleteAfterFailureAsync(
        StoredUpload storedUpload,
        Exception validationException)
    {
        try
        {
            await _uploadStorage.DeleteAsync(storedUpload, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception cleanupException)
            when (cleanupException is IOException
                or UnauthorizedAccessException)
        {
            // Storage ownership is already released, so orphan cleanup can retry without
            // replacing the validation failure that caused this cleanup.
            validationException.Data["EtlTool.UploadCleanupFailure"] = cleanupException;
        }
    }

    private UploadValidationResult FileTooLargeResult()
    {
        return UploadValidationResult.Rejected(CreateFileTooLargeFailure());
    }

    private UploadValidationFailure CreateFileTooLargeFailure()
    {
        return CreateFailure(
            UploadValidationFailureCode.FileTooLarge,
            $"The source file exceeds the configured limit of {_options.MaxFileSizeBytes} bytes.");
    }

    private static UploadValidationResult Rejected(
        UploadValidationFailureCode code,
        string message)
    {
        return UploadValidationResult.Rejected(CreateFailure(code, message));
    }

    private static UploadValidationFailure CreateFailure(
        UploadValidationFailureCode code,
        string message)
    {
        return new UploadValidationFailure(code, message);
    }

    private static SourceType? GetExtensionSourceType(string leafFileName)
    {
        var extension = Path.GetExtension(leafFileName);

        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return SourceType.Csv;
        }

        if (string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return SourceType.Xlsx;
        }

        return null;
    }

    private static long? TryGetRemainingLength(Stream content)
    {
        if (!content.CanSeek)
        {
            return null;
        }

        try
        {
            return Math.Max(0, content.Length - content.Position);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
