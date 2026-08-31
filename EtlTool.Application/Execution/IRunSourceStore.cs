using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Execution;

public interface IRunSourceStore
{
    Task<IEtlSource> OpenAsync(
        EtlRun run,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        EtlRun run,
        CancellationToken cancellationToken);
}
