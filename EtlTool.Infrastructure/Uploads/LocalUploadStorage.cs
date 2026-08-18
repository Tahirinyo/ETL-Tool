using EtlTool.Application.Uploads;

namespace EtlTool.Infrastructure.Uploads;

public sealed class LocalUploadStorage : IUploadStorage
{
    private const int CopyBufferSize = 81_920;
    private const int MaximumNameAttempts = 10;

    private readonly string _rootPath;

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
            File.Move(pendingUpload.PendingPath, pendingUpload.FinalPath, overwrite: false);
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

        File.Delete(expectedPath);
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
