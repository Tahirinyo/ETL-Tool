using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Uploads;
using EtlTool.Domain.Entities;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.Uploads;

namespace EtlTool.Infrastructure.Execution;

public sealed class LocalRunSourceFileStore : IRunSourceStore
{
    private readonly string _rootPath;
    private readonly IUploadStorage _uploadStorage;
    private readonly IFileExtractorResolver _extractorResolver;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public LocalRunSourceFileStore(
        UploadStorageOptions options,
        IUploadStorage uploadStorage,
        IFileExtractorResolver extractorResolver)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(uploadStorage);
        ArgumentNullException.ThrowIfNull(extractorResolver);
        options.Validate();
        _rootPath = Path.GetFullPath(options.RootPath);
        _uploadStorage = uploadStorage;
        _extractorResolver = extractorResolver;
    }

    public Task<IEtlSource> OpenAsync(
        EtlRun run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(run);
        var configuration = run.ExecutionConfiguration
            ?? throw new InvalidOperationException(
                "The admitted ETL execution configuration is unavailable.");
        var extractor = _extractorResolver.Resolve(configuration.SourceType);
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 81_920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        IEtlSource source = new FileEtlSource(
            stream,
            extractor,
            configuration.SourceOptions);
        return Task.FromResult(source);
    }

    public Task ReleaseAsync(EtlRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(run);
        return _uploadStorage.DeleteAsync(
            new StoredUpload(
                OriginalFileName: string.Empty,
                StoredFileName: Path.GetFileName(path),
                StoredFilePath: path,
                SizeInBytes: 0),
            cancellationToken);
    }

    private string Resolve(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.StoredFilePath))
        {
            throw new InvalidOperationException("The ETL run source path is missing.");
        }

        var path = Path.GetFullPath(run.StoredFilePath);
        var fileName = Path.GetFileName(path);
        var relative = Path.GetRelativePath(_rootPath, path);
        if (!IsGeneratedUploadName(fileName)
            || Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(path), _rootPath, _pathComparison))
        {
            throw new InvalidOperationException("The ETL run source path is not a trusted upload artifact.");
        }

        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The ETL run source must not be a link.");
        }

        return path;
    }

    private static bool IsGeneratedUploadName(string fileName) =>
        StoredUploadFileName.TryParse(fileName, out _);
}
