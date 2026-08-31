using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Infrastructure.Extraction;

public sealed class FileEtlSource : IEtlSource
{
    private readonly Stream _stream;
    private readonly IFileExtractor _extractor;
    private readonly SourceOptions _options;
    private int _disposed;

    public FileEtlSource(
        Stream stream,
        IFileExtractor extractor,
        SourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(options);

        if (!stream.CanRead)
        {
            throw new ArgumentException("The file source stream must be readable.", nameof(stream));
        }

        _stream = stream;
        _extractor = extractor;
        _options = CopyOptions(options);
    }

    public async IAsyncEnumerable<DataRow> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        await foreach (var row in _extractor
            .ReadAsync(_stream, _options, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return row;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private static SourceOptions CopyOptions(SourceOptions options) => new()
    {
        CultureName = options.CultureName,
        DateFormat = options.DateFormat,
        Delimiter = options.Delimiter,
        WorksheetName = options.WorksheetName,
        FirstRowIsHeader = options.FirstRowIsHeader
    };
}
