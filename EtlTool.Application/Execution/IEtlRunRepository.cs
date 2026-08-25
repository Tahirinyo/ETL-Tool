using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Execution;

public interface IEtlRunRepository
{
    Task AddAsync(
        EtlRun run,
        CancellationToken cancellationToken);

    Task<EtlRun?> GetByIdAsync(
        Guid runId,
        CancellationToken cancellationToken);

    Task<bool> TryStartAsync(
        Guid runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateProgressAsync(
        Guid runId,
        BatchExecutionProgress progress,
        CancellationToken cancellationToken);

    Task<bool> TryMarkTerminalAsync(
        Guid runId,
        EtlRunStatus status,
        DateTimeOffset completedAt,
        string? systemError,
        CancellationToken cancellationToken);
}
