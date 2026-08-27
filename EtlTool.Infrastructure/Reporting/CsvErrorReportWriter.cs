using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using EtlTool.Application.Processing;
using EtlTool.Application.Reporting;

namespace EtlTool.Infrastructure.Reporting;

public sealed class CsvErrorReportWriter : IErrorReportWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public async Task WriteAsync(
        Stream output,
        Guid runId,
        IReadOnlyList<string> sourceFields,
        string upsertKeyField,
        IAsyncEnumerable<RowProcessingResult> rowResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sourceFields);
        ArgumentNullException.ThrowIfNull(upsertKeyField);
        ArgumentNullException.ThrowIfNull(rowResults);

        if (!output.CanWrite)
        {
            throw new ArgumentException("The error report output stream must be writable.", nameof(output));
        }

        if (runId == Guid.Empty)
        {
            throw new ArgumentException("An error report requires an ETL run identifier.", nameof(runId));
        }

        ValidateSourceFields(sourceFields);
        cancellationToken.ThrowIfCancellationRequested();

        await using var textWriter = new StreamWriter(
            output,
            Utf8WithBom,
            bufferSize: 1024,
            leaveOpen: true);
        using var csvWriter = new CsvWriter(textWriter, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            NewLine = "\r\n"
        }, leaveOpen: true);

        WriteHeader(csvWriter, sourceFields);
        await csvWriter.NextRecordAsync().ConfigureAwait(false);

        await foreach (var result in rowResults
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(result);

            if (result.Status != RowProcessingStatus.Invalid)
            {
                continue;
            }

            foreach (var error in result.Errors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteError(csvWriter, runId, result, error, sourceFields, upsertKeyField);
                await csvWriter.NextRecordAsync().ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await textWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteHeader(CsvWriter csvWriter, IReadOnlyList<string> sourceFields)
    {
        csvWriter.WriteField("RunId");
        csvWriter.WriteField("SourceRowNumber");
        csvWriter.WriteField("ErrorStage");
        csvWriter.WriteField("ErrorField");
        csvWriter.WriteField("RuleId");
        csvWriter.WriteField("TransformationType");
        csvWriter.WriteField("ErrorMessage");
        csvWriter.WriteField("EffectiveUpsertKeyValue");

        foreach (var sourceField in sourceFields)
        {
            csvWriter.WriteField($"Source:{sourceField}");
        }
    }

    private static void WriteError(
        CsvWriter csvWriter,
        Guid runId,
        RowProcessingResult result,
        RowProcessingError error,
        IReadOnlyList<string> sourceFields,
        string upsertKeyField)
    {
        ArgumentNullException.ThrowIfNull(error);

        csvWriter.WriteField(runId.ToString("D", CultureInfo.InvariantCulture));
        csvWriter.WriteField(result.OriginalRow.SourceRowNumber);
        csvWriter.WriteField(error.Stage.ToString());
        csvWriter.WriteField(SanitizeUntrustedText(error.Field));
        csvWriter.WriteField(error.RuleId?.ToString("D", CultureInfo.InvariantCulture));
        csvWriter.WriteField(error.TransformationType?.ToString());
        csvWriter.WriteField(SanitizeUntrustedText(error.Message));
        result.Row.Values.TryGetValue(upsertKeyField, out var upsertKeyValue);
        csvWriter.WriteField(SanitizeUntrustedText(FormatValue(upsertKeyValue)));

        foreach (var sourceField in sourceFields)
        {
            result.OriginalRow.Values.TryGetValue(sourceField, out var value);
            csvWriter.WriteField(SanitizeUntrustedText(FormatValue(value)));
        }
    }

    private static void ValidateSourceFields(IReadOnlyList<string> sourceFields)
    {
        var distinctFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceField in sourceFields)
        {
            if (string.IsNullOrWhiteSpace(sourceField))
            {
                throw new ArgumentException(
                    "The error report source field list contains an empty field name.",
                    nameof(sourceFields));
            }

            if (!distinctFields.Add(sourceField))
            {
                throw new ArgumentException(
                    $"The error report source field list contains duplicate field '{sourceField}'.",
                    nameof(sourceFields));
            }
        }
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        string text => text,
        bool boolean => boolean ? "true" : "false",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(
            $"Source row value type '{value.GetType().FullName}' cannot be represented safely in an error CSV.")
    };

    private static string? SanitizeUntrustedText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var index = 0;
        while (index < value.Length &&
               (char.IsWhiteSpace(value[index]) || char.IsControl(value[index])))
        {
            index++;
        }

        return index < value.Length && value[index] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;
    }
}
