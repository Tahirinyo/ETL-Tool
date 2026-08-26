using EtlTool.Domain.Entities;

namespace EtlTool.Infrastructure.Reporting;

public interface IErrorReportStore
{
    IErrorReportOutput CreateOutput(EtlRun run);

    Stream? OpenRead(EtlRun run);

    Task DeletePublishedAsync(EtlRun run, string reportReference, CancellationToken cancellationToken);
}

public interface IErrorReportOutput : IAsyncDisposable
{
    Stream Stream { get; }

    Task<string> PublishAsync(CancellationToken cancellationToken);

    Task AbortAsync();
}
