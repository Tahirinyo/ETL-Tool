using EtlTool.Domain.Entities;

namespace EtlTool.Infrastructure.Execution;

public interface IRunSourceFileStore
{
    Stream Open(EtlRun run);

    Task DeleteAsync(EtlRun run, CancellationToken cancellationToken);
}
