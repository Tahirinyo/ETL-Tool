using EtlTool.Application.Extraction;
using EtlTool.Application.Sources;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Sources;

public sealed class SourceSchemaInferenceServiceTests
{
    private readonly SourceSchemaInferenceService _service = new();

    [Fact]
    public void Infer_PreservesHeadersAndInfersTextIntegerDecimalAndDate()
    {
        var schema = _service.Infer(
            [" Exact Name ", "Count", "Amount", "Occurred"],
            [
                Row((" Exact Name ", "Ada"), ("Count", "1"), ("Amount", "1.25"), ("Occurred", "2025-01-31")),
                Row((" Exact Name ", "Grace"), ("Count", "2"), ("Amount", "2.50"), ("Occurred", "2025-02-01"))
            ],
            new SourceOptions { CultureName = "en-US" },
            CancellationToken.None);

        Assert.Equal([" Exact Name ", "Count", "Amount", "Occurred"], schema.Select(field => field.Name));
        Assert.Equal(
            [SourceFieldType.String, SourceFieldType.Integer, SourceFieldType.Decimal, SourceFieldType.Date],
            schema.Select(field => field.DataType));
    }

    [Fact]
    public void Infer_IgnoresEmptyValuesAndUsesConservativeMixedEvidence()
    {
        var schema = _service.Infer(
            ["Integer", "Empty", "Numeric", "Mixed", "Conflict"],
            [
                Row(("Integer", null), ("Empty", " "), ("Numeric", "1"), ("Mixed", "1"), ("Conflict", "1")),
                Row(("Integer", "2"), ("Empty", DBNull.Value), ("Numeric", "1.5"), ("Mixed", "text"), ("Conflict", "2025-01-31"))
            ],
            new SourceOptions { CultureName = "en-US" },
            CancellationToken.None);

        Assert.Equal(
            [SourceFieldType.Integer, SourceFieldType.Unknown, SourceFieldType.Decimal, SourceFieldType.String, SourceFieldType.String],
            schema.Select(field => field.DataType));
    }

    [Fact]
    public void Infer_UsesConfiguredCultureExactDateFormatAndNumericPrecedence()
    {
        var turkish = _service.Infer(
            ["Amount", "Date", "Ambiguous"],
            [Row(("Amount", "1,25"), ("Date", "31/12/2025"), ("Ambiguous", "2024"))],
            new SourceOptions { CultureName = "tr-TR", DateFormat = "dd/MM/yyyy" },
            CancellationToken.None);
        var mismatchedFormat = _service.Infer(
            ["Date"],
            [Row(("Date", "2025-12-31"))],
            new SourceOptions { CultureName = "en-US", DateFormat = "dd.MM.yyyy" },
            CancellationToken.None);

        Assert.Equal(
            [SourceFieldType.Decimal, SourceFieldType.Date, SourceFieldType.Integer],
            turkish.Select(field => field.DataType));
        Assert.Equal(SourceFieldType.String, mismatchedFormat.Single().DataType);
    }

    [Fact]
    public void Infer_AppliesIntegerPrecedenceToAmbiguousYearWhenDateFormatIsConfigured()
    {
        var schema = _service.Infer(
            ["Ambiguous"],
            [Row(("Ambiguous", "2024"))],
            new SourceOptions { CultureName = "en-US", DateFormat = "yyyy" },
            CancellationToken.None);

        Assert.Equal(SourceFieldType.Integer, schema.Single().DataType);
    }

    [Fact]
    public void Infer_HandlesNativeXlsxStyleValuesAndUnsupportedValues()
    {
        var schema = _service.Infer(
            ["Integral", "Decimal", "Date", "Unsupported"],
            [Row(("Integral", 2d), ("Decimal", 1.5d), ("Date", new DateTime(2025, 1, 31)), ("Unsupported", true))],
            new SourceOptions(),
            CancellationToken.None);

        Assert.Equal(
            [SourceFieldType.Integer, SourceFieldType.Decimal, SourceFieldType.Date, SourceFieldType.String],
            schema.Select(field => field.DataType));
    }

    [Fact]
    public void Infer_IsDeterministicAndDoesNotMutateRows()
    {
        var row = Row(("Amount", "1.25"));
        var first = _service.Infer(["Amount"], [row], new SourceOptions { CultureName = "en-US" }, CancellationToken.None);
        var second = _service.Infer(["Amount"], [row], new SourceOptions { CultureName = "en-US" }, CancellationToken.None);

        Assert.Equal(first.Single().DataType, second.Single().DataType);
        Assert.Equal("1.25", row.Values["Amount"]);
    }

    [Fact]
    public void Infer_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => _service.Infer(
            ["Value"], [Row(("Value", "1"))], new SourceOptions(), cancellation.Token));
    }

    private static DataRow Row(params (string Name, object? Value)[] values)
    {
        var row = new DataRow();
        foreach (var (name, value) in values)
        {
            row.Values[name] = value;
        }

        return row;
    }
}
