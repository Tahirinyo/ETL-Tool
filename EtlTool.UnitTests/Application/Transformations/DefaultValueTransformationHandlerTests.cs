using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class DefaultValueTransformationHandlerTests
{
    private readonly DefaultValueTransformationHandler _handler = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Apply_AssignsConfiguredDefaultForNullOrEmptyString(string? input)
    {
        var row = Row(12, ("Name", input));

        var result = _handler.Apply(row, Rule("Name", "Unknown"));

        Assert.Same(row, result);
        Assert.Equal("Unknown", row.Values["Name"]);
    }

    [Fact]
    public void Apply_PreservesWhitespaceOnlyString()
    {
        var row = Row(3, ("Name", " \t\r\n "));

        _handler.Apply(row, Rule("Name", "Unknown"));

        Assert.Equal(" \t\r\n ", row.Values["Name"]);
    }

    [Fact]
    public void Apply_PreservesNonEmptyAndNonStringValuesAndRuntimeTypes()
    {
        var row = Row(4, ("Name", "Alice"), ("Count", 0), ("Enabled", false));

        _handler.Apply(row, Rule("Name", "Unknown"));
        _handler.Apply(row, Rule("Count", "Unknown"));
        _handler.Apply(row, Rule("Enabled", "Unknown"));

        Assert.Equal("Alice", row.Values["Name"]);
        Assert.Equal(0, Assert.IsType<int>(row.Values["Count"]));
        Assert.False(Assert.IsType<bool>(row.Values["Enabled"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Apply_AssignsEmptyOrWhitespaceConfiguredDefaultExactly(string defaultValue)
    {
        var row = Row(5, ("Name", null));

        _handler.Apply(row, Rule("Name", defaultValue));

        Assert.Equal(defaultValue, row.Values["Name"]);
    }

    [Fact]
    public void Apply_UsesOrdinalCaseSensitiveFieldAndConfigurationLookup()
    {
        var row = Row(6, ("Name", null), ("name", null));
        var rule = new TransformationRule
        {
            Type = TransformationType.SetDefaultValue,
            SourceField = "name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Value"] = "selected",
                ["value"] = "other"
            }
        };

        _handler.Apply(row, rule);

        Assert.Null(row.Values["Name"]);
        Assert.Equal("selected", row.Values["name"]);
    }

    [Fact]
    public void Apply_PreservesSourceRowNumberAndUnrelatedFields()
    {
        var timestamp = new DateTime(2026, 8, 21, 10, 30, 0, DateTimeKind.Utc);
        var row = Row(27, ("Name", null), ("Count", 7L), ("CreatedAt", timestamp));

        var result = _handler.Apply(row, Rule("Name", "Unknown"));

        Assert.Equal(27, result.SourceRowNumber);
        Assert.Equal("Unknown", result.Values["Name"]);
        Assert.Equal(7L, Assert.IsType<long>(result.Values["Count"]));
        Assert.Equal(timestamp, Assert.IsType<DateTime>(result.Values["CreatedAt"]));
    }

    [Fact]
    public void Apply_ReportsDefaultValueType()
    {
        Assert.Equal(TransformationType.SetDefaultValue, _handler.Type);
    }

    [Fact]
    public void Apply_ThrowsWhenFieldIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(8, ("Name", "Ada")), Rule("Missing", "Unknown")));

        Assert.Contains("Missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Apply_ThrowsWhenSourceFieldIsInvalid(string? sourceField)
    {
        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(9, ("Name", "Ada")), Rule(sourceField, "Unknown")));
    }

    [Fact]
    public void Apply_ThrowsWhenDefaultConfigurationIsMissingCaseMismatchedOrNull()
    {
        var missing = new TransformationRule { SourceField = "Name" };
        var caseMismatched = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "Unknown" }
        };
        var nullValue = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Value"] = null! }
        };

        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", null)), missing));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", null)), caseMismatched));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", null)), nullValue));
    }

    [Fact]
    public void Apply_ThrowsWhenConfigurationContainerIsNull()
    {
        var rule = new TransformationRule { SourceField = "Name", Configuration = null! };

        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(10, ("Name", null)), rule));
    }

    [Fact]
    public void Apply_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(null!, Rule("Name", "Unknown")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(10, ("Name", "Ada")), null!));
    }

    private static TransformationRule Rule(string? sourceField, string defaultValue) => new()
    {
        Type = TransformationType.SetDefaultValue,
        SourceField = sourceField,
        Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Value"] = defaultValue }
    };

    private static DataRow Row(long sourceRowNumber, params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };

        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }
}
