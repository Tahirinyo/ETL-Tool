using EtlTool.Application.Uploads;

namespace EtlTool.Infrastructure.Uploads;

public sealed class LocalUploadStorage : IUploadStorage
{
    private const int CopyBufferSize = 81_920;
    private const int MaximumNameAttempts = 10;

    private readonly string _rootPath;
    private readonly object _ownershipLock = new();
    private readonly HashSet<string> _ownedStoredFileNames = new(StringComparer.Ordinal);

    public LocalUploadStorage(UploadStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _rootPath = Path.GetFullPath(options.RootPath);
    }

    public async Task<StoredUpload> StoreAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException("The uploaded content stream must be readable.", nameof(content));
        }

        var normalizedOriginalFileName = UploadFileName.GetLeafName(originalFileName);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_rootPath);

        string? pendingPath = null;

        try
        {
            var pendingUpload = CreatePendingUpload();
            pendingPath = pendingUpload.PendingPath;
            long sizeInBytes;

            await using (pendingUpload.Stream)
            {
                await content.CopyToAsync(
                    pendingUpload.Stream,
                    CopyBufferSize,
                    cancellationToken);
                await pendingUpload.Stream.FlushAsync(cancellationToken);
                sizeInBytes = pendingUpload.Stream.Length;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_ownershipLock)
            {
                // Final-name publication and ownership acquisition are atomic with orphan deletion.
                _ownedStoredFileNames.Add(pendingUpload.StoredFileName);

                try
                {
                    File.Move(pendingUpload.PendingPath, pendingUpload.FinalPath, overwrite: false);
                }
                catch
                {
                    _ownedStoredFileNames.Remove(pendingUpload.StoredFileName);
                    throw;
                }
            }

            pendingPath = null;

            return new StoredUpload(
                normalizedOriginalFileName,
                pendingUpload.StoredFileName,
                pendingUpload.FinalPath,
                sizeInBytes);
        }
        catch (Exception uploadException)
        {
            try
            {
                if (pendingPath is not null)
                {
                    File.Delete(pendingPath);
                }
            }
            catch (Exception cleanupException)
                when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "The upload failed and its partial file could not be removed.",
                    new AggregateException(uploadException, cleanupException));
            }

            throw;
        }
    }

    public Task DeleteAsync(
        StoredUpload upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryParseStoredFileName(upload.StoredFileName, out var expectedStoredFileName))
        {
            throw new ArgumentException(
                "The stored upload filename is not a service-generated upload name.",
                nameof(upload));
        }

        var expectedPath = Path.GetFullPath(Path.Combine(_rootPath, expectedStoredFileName));
        var suppliedPath = Path.GetFullPath(upload.StoredFilePath);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(expectedPath, suppliedPath, pathComparison))
        {
            throw new ArgumentException(
                "The stored upload path does not match its generated filename and storage root.",
                nameof(upload));
        }

        lock (_ownershipLock)
        {
            // The caller has finished using the upload; a failed delete leaves an unowned orphan
            // that a later age-based cleanup pass can retry.
            _ownedStoredFileNames.Remove(expectedStoredFileName);
            File.Delete(expectedPath);
        }

        return Task.CompletedTask;
    }

    public Task DeleteExpiredAsync(
        DateTimeOffset expiresBefore,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_rootPath))
        {
            return Task.CompletedTask;
        }

        foreach (var path in Directory.EnumerateFiles(_rootPath, "*.upload", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            if (!TryParseStoredFileName(fileName, out _))
            {
                continue;
            }

            lock (_ownershipLock)
            {
                if (_ownedStoredFileNames.Contains(fileName)
                    || !File.Exists(path)
                    || File.GetLastWriteTimeUtc(path) > expiresBefore.UtcDateTime)
                {
                    continue;
                }

                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    private PendingUpload CreatePendingUpload()
    {
        for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            var token = Guid.NewGuid().ToString("N");
            var storedFileName = $"{token}.upload";
            var pendingPath = Path.Combine(_rootPath, $"{token}.partial");
            var finalPath = Path.Combine(_rootPath, storedFileName);

            if (File.Exists(finalPath))
            {
                continue;
            }

            try
            {
                var stream = new FileStream(
                    pendingPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = CopyBufferSize,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });

                return new PendingUpload(storedFileName, pendingPath, finalPath, stream);
            }
            catch (IOException) when (File.Exists(pendingPath) || File.Exists(finalPath))
            {
                // A generated-name collision is safe to retry before any source bytes are consumed.
            }
        }

        throw new IOException("A unique upload filename could not be generated.");
    }

    private static bool TryParseStoredFileName(
        string storedFileName,
        out string expectedStoredFileName)
    {
        expectedStoredFileName = string.Empty;

        if (string.IsNullOrEmpty(storedFileName)
            || Path.GetFileName(storedFileName) != storedFileName
            || !storedFileName.EndsWith(".upload", StringComparison.Ordinal))
        {
            return false;
        }

        var token = storedFileName[..^".upload".Length];

        if (!Guid.TryParseExact(token, "N", out var identifier))
        {
            return false;
        }

        expectedStoredFileName = $"{identifier:N}.upload";
        return string.Equals(storedFileName, expectedStoredFileName, StringComparison.Ordinal);
    }

    private sealed record PendingUpload(
        string StoredFileName,
        string PendingPath,
        string FinalPath,
        FileStream Stream);
}
