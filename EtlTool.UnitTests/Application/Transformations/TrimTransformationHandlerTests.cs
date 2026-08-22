using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class TrimTransformationHandlerTests
{
    private readonly TrimTransformationHandler _handler = new();

    [Theory]
    [InlineData("  Alice  ", "Alice")]
    [InlineData("Alice", "Alice")]
    [InlineData("", "")]
    [InlineData(" \t\r\n ", "")]
    [InlineData(" Alice  Smith ", "Alice  Smith")]
    public void Apply_TrimsOnlyLeadingAndTrailingWhitespace(string input, string expected)
    {
        var row = Row(12, ("Name", input), ("Other", 42L));

        var result = _handler.Apply(row, Rule("Name"));

        Assert.Same(row, result.Row);
        Assert.Equal(expected, row.Values["Name"]);
        Assert.Equal(42L, row.Values["Other"]);
    }

    [Fact]
    public void Apply_PreservesNullValue()
    {
        var row = Row(3, ("Name", null));

        _handler.Apply(row, Rule("Name"));

        Assert.True(row.Values.ContainsKey("Name"));
        Assert.Null(row.Values["Name"]);
    }

    [Fact]
    public void Apply_PreservesNonStringValueAndRuntimeType()
    {
        var timestamp = new DateTime(2026, 8, 21, 10, 30, 0, DateTimeKind.Utc);
        var row = Row(4, ("Value", 42L), ("CreatedAt", timestamp));

        _handler.Apply(row, Rule("Value"));
        _handler.Apply(row, Rule("CreatedAt"));

        Assert.Equal(42L, Assert.IsType<long>(row.Values["Value"]));
        Assert.Equal(timestamp, Assert.IsType<DateTime>(row.Values["CreatedAt"]));
    }

    [Fact]
    public void Apply_UsesOrdinalCaseSensitiveFieldLookup()
    {
        var row = Row(5, ("Name", "  Ada  "), ("name", "  Alias  "));

        _handler.Apply(row, Rule("name"));

        Assert.Equal("  Ada  ", row.Values["Name"]);
        Assert.Equal("Alias", row.Values["name"]);
    }

    [Fact]
    public void Apply_PreservesSourceRowNumberAndUnrelatedFields()
    {
        var row = Row(27, ("Name", "  Ada  "), ("Count", 7), ("Missing", null));
        var originalValues = row.Values.ToArray();

        var result = _handler.Apply(row, Rule("Name"));

        Assert.Equal(27, result.Row.SourceRowNumber);
        Assert.Equal("Ada", result.Row.Values["Name"]);
        Assert.Equal(originalValues[1], new KeyValuePair<string, object?>("Count", result.Row.Values["Count"]));
        Assert.True(result.Row.Values.ContainsKey("Missing"));
        Assert.Null(result.Row.Values["Missing"]);
    }

    [Fact]
    public void Apply_ReportsTrimType()
    {
        Assert.Equal(TransformationType.Trim, _handler.Type);
    }

    [Fact]
    public void Apply_ThrowsWhenFieldIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(8, ("Name", "Ada")), Rule("Missing")));

        Assert.Contains("Missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Apply_ThrowsWhenSourceFieldIsInvalid(string? sourceField)
    {
        var rule = Rule(sourceField);

        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(9, ("Name", "Ada")), rule));
    }

    [Fact]
    public void Apply_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => _handler.Apply(null!, Rule("Name")));
        Assert.Throws<ArgumentNullException>(
            () => _handler.Apply(Row(10, ("Name", "Ada")), null!));
    }

    private static TransformationRule Rule(string? sourceField) => new()
    {
        Type = TransformationType.Trim,
        SourceField = sourceField
    };

    private static DataRow Row(
        long sourceRowNumber,
        params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };

        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }
}
