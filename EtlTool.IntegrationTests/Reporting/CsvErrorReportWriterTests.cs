using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Reporting;

namespace EtlTool.IntegrationTests.Reporting;

public sealed class CsvErrorReportWriterTests
{
    private readonly CsvErrorReportWriter _writer = new();

    [Fact]
    public async Task WriteAsync_WritesUtf8CsvWithStableMetadataAndSourceColumns()
    {
        var runId = Guid.NewGuid();
        var original = Row(
            42,
            ("Name", "Çağrı, \"Ada\""),
            ("Note", "first line\r\nsecond line"),
            ("NullValue", null),
            ("Count", 42L),
            ("When", new DateTime(2026, 8, 26, 12, 34, 56, DateTimeKind.Utc)),
            ("Enabled", true));
        var ruleId = Guid.NewGuid();
        var processed = Row(
            42,
            ("customer_id", "C, \"42\""));
        var result = Invalid(
            original,
            processed,
            new RowProcessingError(
                RowProcessingErrorStage.Transformation,
                "Name",
                "The name cannot be converted.",
                ruleId,
                EtlTool.Domain.Enums.TransformationType.ConvertToInteger));
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            runId,
            ["Name", "Note", "NullValue", "Count", "When", "Enabled"],
            "customer_id",
            Rows(result),
            CancellationToken.None);

        var bytes = output.ToArray();
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.True(output.CanWrite);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"Çağrı, \"\"Ada\"\"\"", text);
        Assert.Contains("\"first line\r\nsecond line\"", text);

        var records = await ReadRecordsAsync(bytes);
        var record = Assert.Single(records);
        Assert.Equal(
            [
                "RunId",
                "SourceRowNumber",
                "ErrorStage",
                "ErrorField",
                "RuleId",
                "TransformationType",
                "ErrorMessage",
                "EffectiveUpsertKeyValue",
                "Source:Name",
                "Source:Note",
                "Source:NullValue",
                "Source:Count",
                "Source:When",
                "Source:Enabled"
            ],
            record.Keys);
        Assert.Equal(runId.ToString("D"), record["RunId"]);
        Assert.Equal("42", record["SourceRowNumber"]);
        Assert.Equal("Transformation", record["ErrorStage"]);
        Assert.Equal("Name", record["ErrorField"]);
        Assert.Equal(ruleId.ToString("D"), record["RuleId"]);
        Assert.Equal("ConvertToInteger", record["TransformationType"]);
        Assert.Equal("The name cannot be converted.", record["ErrorMessage"]);
        Assert.Equal("C, \"42\"", record["EffectiveUpsertKeyValue"]);
        Assert.Equal("Çağrı, \"Ada\"", record["Source:Name"]);
        Assert.Equal("first line\r\nsecond line", record["Source:Note"]);
        Assert.Equal(string.Empty, record["Source:NullValue"]);
        Assert.Equal("42", record["Source:Count"]);
        Assert.Equal("2026-08-26T12:34:56.0000000Z", record["Source:When"]);
        Assert.Equal("true", record["Source:Enabled"]);
    }

    [Fact]
    public async Task WriteAsync_WritesOneRecordPerExistingRowProcessingError()
    {
        var runId = Guid.NewGuid();
        var result = Invalid(
            Row(7, ("Email", "invalid")),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Email", "Email is invalid."),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Name", "Name is required."));
        var validSource = Row(8, ("Email", "valid@example.com"));
        var validResult = RowProcessingResult.Valid(
            validSource,
            new DataRow { SourceRowNumber = validSource.SourceRowNumber });
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            runId,
            ["Email"],
            "email",
            Rows(validResult, result),
            CancellationToken.None);

        var records = await ReadRecordsAsync(output.ToArray());
        Assert.Equal(2, records.Count);
        Assert.Equal(["Email", "Name"], records.Select(record => record["ErrorField"]));
        Assert.All(records, record =>
        {
            Assert.Equal(runId.ToString("D"), record["RunId"]);
            Assert.Equal("7", record["SourceRowNumber"]);
            Assert.Equal("invalid", record["Source:Email"]);
        });
    }

    [Fact]
    public async Task WriteAsync_UsesOriginalSourceValuesFromRealMappedAndTransformedFailures()
    {
        var processor = new PipelineRowProcessor(
            new FieldMappingService(),
            new TransformationEngine(new TransformationHandlerRegistry([new TrimTransformationHandler()])),
            new ValidationEngine(new ValidationHandlerRegistry([new RequiredValidationHandler()])));
        var pipeline = new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions
            {
                Delimiter = CsvDelimiter.Comma,
                FirstRowIsHeader = true
            },
            ExpectedSchema = [new SourceFieldDefinition { Name = "RawValue" }],
            FieldMappings =
            [
                new FieldMapping
                {
                    SourceField = "RawValue",
                    TargetField = "value",
                    IsIncluded = true
                }
            ],
            TransformationRules =
            [
                new TransformationRule
                {
                    Id = Guid.NewGuid(),
                    Order = 1,
                    Type = TransformationType.Trim,
                    SourceField = "value"
                }
            ],
            ValidationRules =
            [
                new ValidationRule
                {
                    Id = Guid.NewGuid(),
                    Type = ValidationType.Required,
                    Field = "value"
                }
            ],
            DestinationDatabase = "demo",
            DestinationCollection = "rows",
            UpsertKeyField = "value"
        };
        var session = processor.CreateSession(pipeline);
        var firstSource = Row(2, ("RawValue", "  "));
        var secondSource = Row(3, ("RawValue", "\t"));
        var firstResult = session.Process(firstSource);
        var secondResult = session.Process(secondSource);
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            Guid.NewGuid(),
            ["RawValue"],
            "value",
            Rows(firstResult, secondResult),
            CancellationToken.None);

        Assert.Equal(RowProcessingStatus.Invalid, firstResult.Status);
        Assert.Equal(RowProcessingStatus.Invalid, secondResult.Status);
        Assert.Equal(string.Empty, firstResult.Row.Values["value"]);
        Assert.Equal(string.Empty, secondResult.Row.Values["value"]);
        Assert.Same(firstSource, firstResult.OriginalRow);
        Assert.Same(secondSource, secondResult.OriginalRow);
        Assert.NotSame(firstResult.OriginalRow, secondResult.OriginalRow);
        Assert.Equal("  ", firstResult.OriginalRow.Values["RawValue"]);
        Assert.Equal("\t", secondResult.OriginalRow.Values["RawValue"]);

        var records = await ReadRecordsAsync(output.ToArray());
        Assert.Equal(["  ", "\t"], records.Select(record => record["Source:RawValue"]));
        Assert.All(records, record => Assert.Equal(string.Empty, record["EffectiveUpsertKeyValue"]));
    }

    [Fact]
    public async Task WriteAsync_UsesMappedAndTransformedUpsertKeyFromProcessedRow()
    {
        var processor = new PipelineRowProcessor(
            new FieldMappingService(),
            new TransformationEngine(new TransformationHandlerRegistry(
            [
                new TrimTransformationHandler(),
                new ToUpperTransformationHandler()
            ])),
            new ValidationEngine(new ValidationHandlerRegistry([new RequiredValidationHandler()])));
        var pipeline = new PipelineDefinition
        {
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "RawCustomerId" },
                new SourceFieldDefinition { Name = "RawName" }
            ],
            FieldMappings =
            [
                new FieldMapping { SourceField = "RawCustomerId", TargetField = "customer_id" },
                new FieldMapping { SourceField = "RawName", TargetField = "name" }
            ],
            TransformationRules =
            [
                new TransformationRule { Order = 1, Type = TransformationType.Trim, SourceField = "customer_id" },
                new TransformationRule { Order = 2, Type = TransformationType.ToUpper, SourceField = "customer_id" }
            ],
            ValidationRules =
            [
                new ValidationRule { Type = ValidationType.Required, Field = "name" }
            ],
            UpsertKeyField = "customer_id"
        };
        var source = Row(2, ("RawCustomerId", "  customer-42  "), ("RawName", ""));
        var result = processor.CreateSession(pipeline).Process(source);
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            Guid.NewGuid(),
            ["RawCustomerId", "RawName"],
            pipeline.UpsertKeyField,
            Rows(result),
            CancellationToken.None);

        var record = Assert.Single(await ReadRecordsAsync(output.ToArray()));
        Assert.Equal(RowProcessingStatus.Invalid, result.Status);
        Assert.Equal("CUSTOMER-42", record["EffectiveUpsertKeyValue"]);
        Assert.Equal("  customer-42  ", record["Source:RawCustomerId"]);
    }

    [Fact]
    public async Task WriteAsync_RepresentsMissingAndNullEffectiveUpsertKeysAsEmpty()
    {
        var missing = Invalid(
            Row(2, ("Input", "missing")),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        var nullValue = Invalid(
            Row(3, ("Input", "null")),
            Row(3, ("effective_key", null)),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            Guid.NewGuid(),
            ["Input"],
            "effective_key",
            Rows(missing, nullValue),
            CancellationToken.None);

        var records = await ReadRecordsAsync(output.ToArray());
        Assert.All(records, record => Assert.Equal(string.Empty, record["EffectiveUpsertKeyValue"]));
    }

    [Fact]
    public async Task WriteAsync_FormatsNormalEffectiveUpsertKeyValues()
    {
        var date = new DateTime(2026, 8, 27, 10, 20, 30, DateTimeKind.Utc);
        var stringValue = Invalid(
            Row(2, ("Input", "string")),
            Row(2, ("effective_key", "key")),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        var numericValue = Invalid(
            Row(3, ("Input", "numeric")),
            Row(3, ("effective_key", 42.5m)),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        var dateValue = Invalid(
            Row(4, ("Input", "date")),
            Row(4, ("effective_key", date)),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            Guid.NewGuid(),
            ["Input"],
            "effective_key",
            Rows(stringValue, numericValue, dateValue),
            CancellationToken.None);

        var records = await ReadRecordsAsync(output.ToArray());
        Assert.Equal(
            ["key", "42.5", "2026-08-27T10:20:30.0000000Z"],
            records.Select(record => record["EffectiveUpsertKeyValue"]));
    }

    [Theory]
    [InlineData("=SUM(A1:A2)")]
    [InlineData("+1+1")]
    [InlineData("-10")]
    [InlineData("@SUM(A1:A2)")]
    [InlineData(" \t=SUM(A1:A2)")]
    [InlineData("\r\n-1+2")]
    [InlineData("\u0000+1+1")]
    [InlineData("=SUM(\"A,B\")")]
    [InlineData("=1+1\r\nsecond line")]
    public async Task WriteAsync_PrefixesPotentialSpreadsheetFormulasInUntrustedCells(string dangerousValue)
    {
        var result = Invalid(
            Row(2, ("Input", dangerousValue)),
            Row(2, ("effective_key", dangerousValue)),
            new RowProcessingError(RowProcessingErrorStage.Validation, "=Field", "@unsafe message"));
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            Guid.NewGuid(),
            ["Input"],
            "effective_key",
            Rows(result),
            CancellationToken.None);

        var record = Assert.Single(await ReadRecordsAsync(output.ToArray()));
        Assert.Equal("'" + dangerousValue, record["EffectiveUpsertKeyValue"]);
        Assert.Equal("'" + dangerousValue, record["Source:Input"]);
        Assert.Equal("'=Field", record["ErrorField"]);
        Assert.Equal("'@unsafe message", record["ErrorMessage"]);
    }

    [Fact]
    public async Task WriteAsync_LeavesOrdinaryUntrustedTextUnchanged()
    {
        var result = Invalid(
            Row(2, ("Input", "Ada Yılmaz"), ("Empty", string.Empty)),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "A normal message."));
        await using var output = new MemoryStream();

        await _writer.WriteAsync(
            output,
            Guid.NewGuid(),
            ["Input", "Empty"],
            "effective_key",
            Rows(result),
            CancellationToken.None);

        var record = Assert.Single(await ReadRecordsAsync(output.ToArray()));
        Assert.Equal("Ada Yılmaz", record["Source:Input"]);
        Assert.Equal(string.Empty, record["Source:Empty"]);
        Assert.Equal("Input", record["ErrorField"]);
        Assert.Equal("A normal message.", record["ErrorMessage"]);
    }

    [Fact]
    public async Task WriteAsync_EnumeratesResultsSequentiallyAndLeavesCallerOutputOpen()
    {
        var firstResult = Invalid(
            Row(2, ("Input", "first")),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        var secondResult = Invalid(
            Row(3, ("Input", "second")),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        var secondEnumerationRequested = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondResult = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var output = new MemoryStream();

        var writing = _writer.WriteAsync(
            output,
            Guid.NewGuid(),
            ["Input"],
            "effective_key",
            ControlledRows(
                firstResult,
                secondResult,
                secondEnumerationRequested,
                allowSecondResult),
            CancellationToken.None);

        await secondEnumerationRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(writing.IsCompleted);
        allowSecondResult.SetResult(true);
        await writing;

        Assert.True(output.CanWrite);
        Assert.Equal(2, (await ReadRecordsAsync(output.ToArray())).Count);
    }

    [Fact]
    public async Task WriteAsync_PropagatesCancellationAndOutputFailures()
    {
        using var cancellation = new CancellationTokenSource();
        var firstResult = Invalid(
            Row(2, ("Input", "first")),
            new RowProcessingError(RowProcessingErrorStage.Validation, "Input", "Invalid."));
        var secondEnumerationRequested = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cancellationOutput = new MemoryStream();
        var writing = _writer.WriteAsync(
            cancellationOutput,
            Guid.NewGuid(),
            ["Input"],
            "effective_key",
            ControlledRows(firstResult, firstResult, secondEnumerationRequested, neverRelease),
            cancellation.Token);

        await secondEnumerationRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.True(cancellationOutput.CanWrite);

        await using var failingOutput = new ThrowingWriteStream();
        await Assert.ThrowsAsync<IOException>(() => _writer.WriteAsync(
            failingOutput,
            Guid.NewGuid(),
            ["Input"],
            "effective_key",
            Rows(firstResult),
            CancellationToken.None));
        Assert.True(failingOutput.CanWrite);
    }

    private static RowProcessingResult Invalid(
        DataRow originalRow,
        params RowProcessingError[] errors) =>
        Invalid(
            originalRow,
            new DataRow { SourceRowNumber = originalRow.SourceRowNumber },
            errors);

    private static RowProcessingResult Invalid(
        DataRow originalRow,
        DataRow processedRow,
        params RowProcessingError[] errors)
    {
        return RowProcessingResult.Invalid(originalRow, processedRow, errors);
    }

    private static DataRow Row(long rowNumber, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = rowNumber };
        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }

    private static async IAsyncEnumerable<RowProcessingResult> Rows(
        params RowProcessingResult[] results)
    {
        foreach (var result in results)
        {
            yield return result;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<RowProcessingResult> ControlledRows(
        RowProcessingResult first,
        RowProcessingResult second,
        TaskCompletionSource<bool> secondEnumerationRequested,
        TaskCompletionSource<bool> allowSecondResult,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return first;
        secondEnumerationRequested.SetResult(true);
        await allowSecondResult.Task.WaitAsync(cancellationToken);
        yield return second;
    }

    private static async Task<IReadOnlyList<Dictionary<string, string>>> ReadRecordsAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        using var textReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csvReader = new CsvReader(textReader, new CsvConfiguration(CultureInfo.InvariantCulture));
        Assert.True(await csvReader.ReadAsync());
        Assert.True(csvReader.ReadHeader());
        var headers = csvReader.HeaderRecord!;
        var records = new List<Dictionary<string, string>>();

        while (await csvReader.ReadAsync())
        {
            records.Add(headers.ToDictionary(
                header => header,
                header => csvReader.GetField(header) ?? string.Empty,
                StringComparer.Ordinal));
        }

        return records;
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new IOException("The output failed.");

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("The output failed."));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("The output failed.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The output failed."));
    }
}
