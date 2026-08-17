using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Extraction;

/// <summary>
/// Extracts source rows as a deferred asynchronous stream.
/// </summary>
/// <remarks>
/// The caller owns the input stream. Implementations must not dispose it and are expected to observe
/// cancellation while reading and yielding rows.
/// </remarks>
public interface IFileExtractor
{
    IAsyncEnumerable<DataRow> ReadAsync(
        Stream stream,
        SourceOptions options,
        CancellationToken cancellationToken);
}
