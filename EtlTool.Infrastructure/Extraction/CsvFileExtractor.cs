using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Infrastructure.Extraction;

public sealed class CsvFileExtractor : IFileExtractor
{
    public SourceType SourceType => SourceType.Csv;

    public async Task<IReadOnlyList<string>> ReadHeadersAsync(
        Stream stream,
        SourceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(stream));
        }

        var configuration = new CsvConfiguration(options.ResolveCulture())
        {
            Delimiter = GetDelimiter(options.Delimiter), DetectColumnCountChanges = false,
            DetectDelimiter = false, HasHeaderRecord = true, IgnoreBlankLines = true
        };
        using var textReader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
        using var csvReader = new CsvReader(textReader, configuration, leaveOpen: true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await ReadNextAsync(csvReader).ConfigureAwait(false))
        {
            throw new InvalidDataException("The CSV input must contain a header record.");
        }
        var headers = ReadHeaders(csvReader);
        ValidateHeaders(headers);
        return headers;
    }

    public async IAsyncEnumerable<DataRow> ReadAsync(
        Stream stream,
        SourceOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        if (!stream.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(stream));
        }

        if (!options.FirstRowIsHeader)
        {
            throw new NotSupportedException("CSV extraction without a header row is not supported.");
        }

        var configuration = new CsvConfiguration(options.ResolveCulture())
        {
            Delimiter = GetDelimiter(options.Delimiter),
            DetectColumnCountChanges = false,
            DetectDelimiter = false,
            HasHeaderRecord = true,
            IgnoreBlankLines = true
        };

        using var textReader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        using var csvReader = new CsvReader(textReader, configuration, leaveOpen: true);

        cancellationToken.ThrowIfCancellationRequested();

        if (!await ReadNextAsync(csvReader).ConfigureAwait(false))
        {
            throw new InvalidDataException("The CSV input must contain a header record.");
        }

        var headers = ReadHeaders(csvReader);

        ValidateHeaders(headers);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await ReadNextAsync(csvReader).ConfigureAwait(false))
            {
                yield break;
            }

            var record = ReadCurrentRecord(csvReader);

            if (record.Length > headers.Length)
            {
                throw new InvalidDataException(
                    $"CSV record {csvReader.Parser.Row} contains more fields than the header record.");
            }

            var row = new DataRow { SourceRowNumber = csvReader.Parser.Row };

            for (var index = 0; index < headers.Length; index++)
            {
                row.Values[headers[index]] = index < record.Length ? record[index] : null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    private static string GetDelimiter(CsvDelimiter? delimiter)
    {
        return delimiter switch
        {
            null or CsvDelimiter.Comma => ",",
            CsvDelimiter.Semicolon => ";",
            CsvDelimiter.Tab => "\t",
            _ => throw new ArgumentOutOfRangeException(
                nameof(delimiter),
                delimiter,
                "The CSV delimiter is not supported.")
        };
    }

    private static void ValidateHeaders(string[] headers)
    {
        var distinctHeaders = new HashSet<string>(StringComparer.Ordinal);

        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                throw new InvalidDataException("CSV header names cannot be empty or whitespace.");
            }

            if (!distinctHeaders.Add(header))
            {
                throw new InvalidDataException($"CSV header '{header}' is duplicated.");
            }
        }
    }

    private static async Task<bool> ReadNextAsync(CsvReader csvReader)
    {
        try
        {
            return await csvReader.ReadAsync().ConfigureAwait(false);
        }
        catch (BadDataException exception)
        {
            throw new InvalidDataException("The CSV input is malformed.", exception);
        }
        catch (ParserException exception)
        {
            throw new InvalidDataException("The CSV input is malformed.", exception);
        }
    }

    private static string[] ReadHeaders(CsvReader csvReader)
    {
        try
        {
            if (!csvReader.ReadHeader() || csvReader.HeaderRecord is not { } headers)
            {
                throw new InvalidDataException("The CSV input must contain a header record.");
            }

            return headers;
        }
        catch (BadDataException exception)
        {
            throw new InvalidDataException("The CSV input is malformed.", exception);
        }
        catch (ParserException exception)
        {
            throw new InvalidDataException("The CSV input is malformed.", exception);
        }
    }

    private static string[] ReadCurrentRecord(CsvReader csvReader)
    {
        try
        {
            return csvReader.Parser.Record
                ?? throw new InvalidDataException("The CSV parser did not return the current record.");
        }
        catch (BadDataException exception)
        {
            throw new InvalidDataException("The CSV input is malformed.", exception);
        }
        catch (ParserException exception)
        {
            throw new InvalidDataException("The CSV input is malformed.", exception);
        }
    }
}
