using EtlTool.Domain.Entities;

namespace EtlTool.Infrastructure.Reporting;

public sealed class LocalErrorReportStore : IErrorReportStore
{
    private readonly string _rootPath;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public LocalErrorReportStore(ErrorReportStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _rootPath = Path.GetFullPath(options.RootPath);
    }

    public IErrorReportOutput CreateOutput(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateRunId(run.Id);
        Directory.CreateDirectory(_rootPath);

        var temporaryName = $".{GetReference(run.Id)}-{Guid.NewGuid():N}.partial";
        var temporaryPath = ResolveLeafPath(temporaryName);
        var stream = new FileStream(temporaryPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        return new LocalErrorReportOutput(this, run.Id, temporaryPath, stream);
    }

    public Stream? OpenRead(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!TryResolveOwnedReference(run, run.ErrorReportPath, out var path))
        {
            return null;
        }

        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81_920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public Task DeletePublishedAsync(EtlRun run, string reportReference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        if (run.Id == Guid.Empty
            || !string.Equals(reportReference, GetReference(run.Id), StringComparison.Ordinal))
        {
            throw new ArgumentException("The error report reference is not owned by this run.", nameof(reportReference));
        }

        File.Delete(ResolveLeafPath(reportReference));
        return Task.CompletedTask;
    }

    private string Publish(Guid runId, string temporaryPath)
    {
        var reference = GetReference(runId);
        var destinationPath = ResolveLeafPath(reference);
        File.Move(temporaryPath, destinationPath, overwrite: false);
        return reference;
    }

    private bool TryResolveOwnedReference(EtlRun run, string? reference, out string path)
    {
        path = string.Empty;
        if (run.Id == Guid.Empty || !string.Equals(reference, GetReference(run.Id), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            path = ResolveLeafPath(reference!);
            return File.Exists(path)
                && !File.GetAttributes(path).HasFlag(FileAttributes.Directory)
                && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string ResolveLeafPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The error report reference must be a generated leaf filename.", nameof(fileName));
        }

        var path = Path.GetFullPath(Path.Combine(_rootPath, fileName));
        var relative = Path.GetRelativePath(_rootPath, path);
        if (Path.IsPathFullyQualified(relative) || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(path), _rootPath, _pathComparison))
        {
            throw new ArgumentException("The error report reference escapes its storage root.", nameof(fileName));
        }

        return path;
    }

    private static string GetReference(Guid runId) => $"error-report-{runId:N}.csv";

    private static void ValidateRunId(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("An error report requires an ETL run identifier.", nameof(runId));
        }
    }

    private sealed class LocalErrorReportOutput(
        LocalErrorReportStore store,
        Guid runId,
        string temporaryPath,
        FileStream stream) : IErrorReportOutput
    {
        private bool _published;
        private bool _disposed;

        public Stream Stream => stream;

        public async Task<string> PublishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_published)
            {
                return GetReference(runId);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
            var reference = store.Publish(runId, temporaryPath);
            _published = true;
            return reference;
        }

        public Task AbortAsync()
        {
            if (!_disposed)
            {
                stream.Dispose();
                _disposed = true;
            }

            if (!_published)
            {
                File.Delete(temporaryPath);
            }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_published)
            {
                await AbortAsync().ConfigureAwait(false);
            }
        }
    }
}
