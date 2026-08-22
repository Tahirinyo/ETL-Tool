using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class FindAndReplaceTransformationHandlerTests
{
    private readonly FindAndReplaceTransformationHandler _handler = new();

    [Theory]
    [InlineData("hello world", "world", "there", "hello there")]
    [InlineData("foo foo", "foo", "bar", "bar bar")]
    [InlineData("Foo foo", "foo", "bar", "Foo bar")]
    [InlineData("hello", "x", "y", "hello")]
    [InlineData("hello", "ell", "", "ho")]
    [InlineData("", "x", "y", "")]
    [InlineData("a  b", " ", "-", "a--b")]
    public void Apply_ReplacesAllOrdinalOccurrencesInStringValues(
        string input,
        string find,
        string replace,
        string expected)
    {
        var row = Row(12, ("Name", input), ("Other", 42L));

        var result = _handler.Apply(row, Rule("Name", find, replace));

        Assert.Same(row, result.Row);
        Assert.Equal(expected, row.Values["Name"]);
        Assert.Equal(42L, Assert.IsType<long>(row.Values["Other"]));
    }

    [Fact]
    public void Apply_AllowsWhitespaceOnlyFindAndReplacementValues()
    {
        var row = Row(3, ("Name", "a  b"));

        _handler.Apply(row, Rule("Name", "  ", "\t"));

        Assert.Equal("a\tb", row.Values["Name"]);
    }

    [Fact]
    public void Apply_UsesOrdinalMatchingIndependentOfAmbientCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var row = Row(3, ("Name", "Iıiİ"));

            _handler.Apply(row, Rule("Name", "i", "x"));

            Assert.Equal("Iıxİ", row.Values["Name"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Apply_PreservesNullAndNonStringValuesAndRuntimeTypes()
    {
        var timestamp = new DateTime(2026, 8, 21, 10, 30, 0, DateTimeKind.Utc);
        var row = Row(4, ("NullValue", null), ("Count", 10), ("CreatedAt", timestamp));

        _handler.Apply(row, Rule("NullValue", "x", "y"));
        _handler.Apply(row, Rule("Count", "1", "2"));
        _handler.Apply(row, Rule("CreatedAt", "2026", "2027"));

        Assert.Null(row.Values["NullValue"]);
        Assert.Equal(10, Assert.IsType<int>(row.Values["Count"]));
        Assert.Equal(timestamp, Assert.IsType<DateTime>(row.Values["CreatedAt"]));
    }

    [Fact]
    public void Apply_UsesOrdinalCaseSensitiveFieldAndConfigurationLookup()
    {
        var row = Row(27, ("Name", "Foo"), ("name", "foo"), ("Other", 7));
        var rule = new TransformationRule
        {
            Type = TransformationType.FindAndReplace,
            SourceField = "name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Find"] = "foo",
                ["find"] = "ignored",
                ["Replace"] = "bar",
                ["replace"] = "ignored"
            }
        };

        var result = _handler.Apply(row, rule);

        Assert.Equal(27, result.Row.SourceRowNumber);
        Assert.Equal("Foo", result.Row.Values["Name"]);
        Assert.Equal("bar", result.Row.Values["name"]);
        Assert.Equal(7, result.Row.Values["Other"]);
    }

    [Fact]
    public void Apply_ReportsFindAndReplaceType()
    {
        Assert.Equal(TransformationType.FindAndReplace, _handler.Type);
    }

    [Fact]
    public void Apply_ThrowsWhenFieldIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(8, ("Name", "Ada")), Rule("Missing", "A", "B")));

        Assert.Contains("Missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Apply_ThrowsWhenSourceFieldIsInvalid(string? sourceField)
    {
        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(9, ("Name", "Ada")), Rule(sourceField, "A", "B")));
    }

    [Fact]
    public void Apply_ThrowsWhenConfigurationIsMissingCaseMismatchedOrNull()
    {
        var missingFind = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Replace"] = "B" }
        };
        var missingReplace = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Find"] = "A" }
        };
        var caseMismatched = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["find"] = "A",
                ["replace"] = "B"
            }
        };
        var caseMismatchedReplace = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Find"] = "A",
                ["replace"] = "B"
            }
        };
        var nullFind = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Find"] = null!,
                ["Replace"] = "B"
            }
        };
        var nullReplace = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Find"] = "A",
                ["Replace"] = null!
            }
        };

        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", "Ada")), missingFind));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", "Ada")), missingReplace));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", "Ada")), caseMismatched));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", "Ada")), caseMismatchedReplace));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", "Ada")), nullFind));
        Assert.Throws<InvalidOperationException>(() => _handler.Apply(Row(10, ("Name", "Ada")), nullReplace));
    }

    [Fact]
    public void Apply_RejectsCaseMismatchedConfigurationKeysRegardlessOfDictionaryComparer()
    {
        var rule = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["find"] = "A",
                ["replace"] = "B"
            }
        };

        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(10, ("Name", "Ada")), rule));
    }

    [Fact]
    public void Apply_AcceptsCorrectlyCasedConfigurationKeysRegardlessOfDictionaryComparer()
    {
        var row = Row(10, ("Name", "Ada"));
        var rule = new TransformationRule
        {
            SourceField = "Name",
            Configuration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Find"] = "A",
                ["Replace"] = "B"
            }
        };

        _handler.Apply(row, rule);

        Assert.Equal("Bda", row.Values["Name"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Apply_ThrowsWhenFindValueIsEmptyOrNull(string? find)
    {
        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(11, ("Name", "Ada")), Rule("Name", find!, "B")));
    }

    [Fact]
    public void Apply_ThrowsWhenConfigurationContainerIsNull()
    {
        var rule = new TransformationRule { SourceField = "Name", Configuration = null! };

        Assert.Throws<InvalidOperationException>(
            () => _handler.Apply(Row(12, ("Name", "Ada")), rule));
    }

    [Fact]
    public void Apply_ThrowsWhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(null!, Rule("Name", "A", "B")));
        Assert.Throws<ArgumentNullException>(() => _handler.Apply(Row(13, ("Name", "Ada")), null!));
    }

    private static TransformationRule Rule(string? sourceField, string find, string replace) => new()
    {
        Type = TransformationType.FindAndReplace,
        SourceField = sourceField,
        Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Find"] = find,
            ["Replace"] = replace
        }
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
