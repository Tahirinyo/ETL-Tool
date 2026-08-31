namespace EtlTool.Application.Extraction;

/// <summary>
/// Provides deferred ETL source rows and owns the resources required to read them.
/// </summary>
public interface IEtlSource : IAsyncDisposable
{
    IAsyncEnumerable<DataRow> ReadAsync(CancellationToken cancellationToken);
}
