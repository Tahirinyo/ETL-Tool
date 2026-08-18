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
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
        {
            throw new IOException(
                "Upload validation failed and the stored upload could not be removed.",
                new AggregateException(validationException, cleanupException));
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
