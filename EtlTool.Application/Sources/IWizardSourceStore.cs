using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Sources;

public interface IWizardSourceStore
{
    Task<bool> ActivateAsync(
        Guid pipelineId,
        Guid sourceReferenceId,
        CancellationToken cancellationToken);

    Task DiscardAsync(
        Guid sourceReferenceId,
        CancellationToken cancellationToken);

    Task<IWizardSourceLease?> AcquireAsync(
        Guid pipelineId,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken);

    Task<IWizardRunSourceReservation?> ReserveForRunAsync(
        Guid pipelineId,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken) =>
        Task.FromResult<IWizardRunSourceReservation?>(null);

    Task RemoveAsync(
        Guid pipelineId,
        CancellationToken cancellationToken);

    Task RetireActiveAsync(
        Guid pipelineId,
        CancellationToken cancellationToken);
}

public interface IWizardSourceLease : IEtlSource;

public interface IWizardRunSourceReservation : IAsyncDisposable
{
    string OriginalFileName { get; }

    string StoredFilePath { get; }

    void TransferToRun();
}
