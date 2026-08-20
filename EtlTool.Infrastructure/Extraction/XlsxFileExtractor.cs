using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using EtlTool.Application.Extraction;
using EtlTool.Domain.ValueObjects;
using ExcelDataReader;
using ExcelDataReader.Exceptions;

namespace EtlTool.Infrastructure.Extraction;

public sealed class XlsxFileExtractor : IFileExtractor
{
    static XlsxFileExtractor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<IReadOnlyList<string>> ReadHeadersAsync(
        Stream stream,
        SourceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WorksheetName))
        {
            throw new ArgumentException("An XLSX worksheet name must be configured.", nameof(options));
        }
        await Task.CompletedTask.ConfigureAwait(false);
        using var reader = CreateReader(stream);
        if (!MoveToWorksheet(reader, options.WorksheetName, cancellationToken) || !Read(reader, cancellationToken))
        {
            throw new InvalidDataException($"The XLSX worksheet '{options.WorksheetName}' must contain a header row.");
        }
        return ReadHeaders(reader);
    }

    public async Task<IReadOnlyList<string>> GetWorksheetNamesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The XLSX input stream must be readable and seekable.", nameof(stream));
        }
        await Task.CompletedTask.ConfigureAwait(false);
        using var reader = CreateReader(stream);
        var names = new List<string>();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            names.Add(reader.Name);
        } while (TranslateFormatErrors(reader.NextResult));
        return names;
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

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The XLSX input stream must be seekable.", nameof(stream));
        }

        if (!options.FirstRowIsHeader)
        {
            throw new NotSupportedException("XLSX extraction without a header row is not supported.");
        }

        if (string.IsNullOrWhiteSpace(options.WorksheetName))
        {
            throw new ArgumentException(
                "An XLSX worksheet name must be configured.",
                nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ExcelDataReader exposes synchronous workbook operations. The completed await keeps the
        // public asynchronous iterator contract while cancellation is observed around each call.
        await Task.CompletedTask.ConfigureAwait(false);

        using var reader = CreateReader(stream);

        if (!MoveToWorksheet(reader, options.WorksheetName, cancellationToken))
        {
            throw new InvalidDataException(
                $"The XLSX worksheet '{options.WorksheetName}' does not exist.");
        }

        if (!Read(reader, cancellationToken))
        {
            throw new InvalidDataException(
                $"The XLSX worksheet '{reader.Name}' must contain a header row.");
        }

        var headers = ReadHeaders(reader);
        var logicalRowNumber = 1L;

        while (Read(reader, cancellationToken))
        {
            var values = ReadValues(reader);

            if (values.All(value => value is null))
            {
                continue;
            }

            if (values.Skip(headers.Length).Any(value => value is not null))
            {
                throw new InvalidDataException(
                    $"XLSX record {logicalRowNumber + 1} contains more fields than the header row.");
            }

            logicalRowNumber++;
            var row = new DataRow { SourceRowNumber = logicalRowNumber };

            for (var index = 0; index < headers.Length; index++)
            {
                row.Values[headers[index]] = index < values.Length ? values[index] : null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    private static IExcelDataReader CreateReader(Stream stream)
    {
        return TranslateFormatErrors(
            () => ExcelReaderFactory.CreateOpenXmlReader(
                stream,
                new ExcelReaderConfiguration
                {
                    LeaveOpen = true,
                    SinglePassMode = true
                }));
    }

    private static bool MoveToWorksheet(
        IExcelDataReader reader,
        string worksheetName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(reader.Name, worksheetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var hasNextWorksheet = TranslateFormatErrors(reader.NextResult);
            cancellationToken.ThrowIfCancellationRequested();

            if (!hasNextWorksheet)
            {
                return false;
            }
        }
    }

    private static bool Read(
        IExcelDataReader reader,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasRow = TranslateFormatErrors(reader.Read);
        cancellationToken.ThrowIfCancellationRequested();
        return hasRow;
    }

    private static string[] ReadHeaders(IExcelDataReader reader)
    {
        var headers = ReadValues(reader)
            .Select(ToHeaderName)
            .ToArray();
        var distinctHeaders = new HashSet<string>(StringComparer.Ordinal);

        if (headers.Length == 0)
        {
            throw new InvalidDataException("XLSX header names cannot be empty or whitespace.");
        }

        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                throw new InvalidDataException("XLSX header names cannot be empty or whitespace.");
            }

            if (!distinctHeaders.Add(header))
            {
                throw new InvalidDataException($"XLSX header '{header}' is duplicated.");
            }
        }

        return headers;
    }

    private static object?[] ReadValues(IExcelDataReader reader)
    {
        var values = new object?[reader.FieldCount];

        for (var index = 0; index < values.Length; index++)
        {
            var value = TranslateFormatErrors(() => reader.GetValue(index));

            if (value is null && TranslateFormatErrors(() => reader.GetFieldType(index)) == typeof(string))
            {
                values[index] = string.Empty;
            }
            else
            {
                values[index] = value is DBNull ? null : value;
            }
        }

        return values;
    }

    private static string ToHeaderName(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static T TranslateFormatErrors<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (ExcelReaderException exception)
        {
            throw new InvalidDataException("The XLSX input is malformed.", exception);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The XLSX input is malformed.", exception);
        }
    }
}
