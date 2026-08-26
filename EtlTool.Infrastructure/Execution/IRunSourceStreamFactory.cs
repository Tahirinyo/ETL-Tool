using EtlTool.Domain.Entities;

namespace EtlTool.Infrastructure.Execution;

internal interface IRunSourceStreamFactory
{
    Stream Open(EtlRun run);
}

internal sealed class RunSourceStreamFactory : IRunSourceStreamFactory
{
    public Stream Open(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (string.IsNullOrWhiteSpace(run.StoredFilePath))
        {
            throw new InvalidOperationException("The ETL run source path is missing.");
        }

        return new FileStream(
            Path.GetFullPath(run.StoredFilePath),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81_920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
    }
}
