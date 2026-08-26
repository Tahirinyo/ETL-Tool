using EtlTool.Domain.Entities;

namespace EtlTool.Infrastructure.Execution;

internal interface IErrorReportOutputStreamFactory
{
    Stream Open(EtlRun run);
}

internal sealed class TransientErrorReportOutputStreamFactory : IErrorReportOutputStreamFactory
{
    public Stream Open(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var path = Path.Combine(
            Path.GetTempPath(),
            $"etltool-errors-{run.Id:N}-{Path.GetRandomFileName()}.csv");
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous
                | FileOptions.SequentialScan
                | FileOptions.DeleteOnClose
        });
    }
}
