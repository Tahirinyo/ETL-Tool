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

        var normalizedOriginalFileName = GetLeafFileName(originalFileName);
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

    private static string GetLeafFileName(string originalFileName)
    {
        ArgumentNullException.ThrowIfNull(originalFileName);

        var normalizedSeparators = originalFileName.Replace('\\', '/');
        var lastSeparator = normalizedSeparators.LastIndexOf('/');
        var leafFileName = normalizedSeparators[(lastSeparator + 1)..];

        if (string.IsNullOrWhiteSpace(leafFileName)
            || leafFileName is "." or "..")
        {
            throw new ArgumentException(
                "The original filename must contain a non-empty leaf filename.",
                nameof(originalFileName));
        }

        return leafFileName;
    }

    private sealed record PendingUpload(
        string StoredFileName,
        string PendingPath,
        string FinalPath,
        FileStream Stream);
}
