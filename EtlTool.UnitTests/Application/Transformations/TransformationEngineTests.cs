using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class TransformationEngineTests
{
    [Fact]
    public void Apply_ExecutesRulesByPersistedOrderAcrossMultipleHandlers()
    {
        var calls = new List<string>();
        var trim = Handler(TransformationType.Trim, (row, rule) =>
        {
            calls.Add($"trim:{rule.Order}");
            return row;
        });
        var toLower = Handler(TransformationType.ToLower, (row, rule) =>
        {
            calls.Add($"lower:{rule.Order}");
            return row;
        });
        var engine = Engine(trim, toLower);
        var rules = new List<TransformationRule>
        {
            Rule(30, TransformationType.Trim),
            Rule(10, TransformationType.ToLower),
            Rule(20, TransformationType.Trim)
        };

        engine.Apply(Row(7, ("Name", "Ada")), rules);

        Assert.Equal(["lower:10", "trim:20", "trim:30"], calls);
        Assert.Equal([30, 10, 20], rules.Select(rule => rule.Order));
    }

    [Fact]
    public void Apply_PassesEachHandlerOutputToTheNextHandler()
    {
        var source = Row(12, ("Name", "Ada"), ("Unchanged", 42L));
        DataRow? firstOutput = null;
        DataRow? secondInput = null;
        var first = Handler(TransformationType.Trim, (row, _) =>
        {
            firstOutput = Row(
                row.SourceRowNumber,
                ("Name", "Grace"),
                ("Unchanged", row.Values["Unchanged"]));
            return firstOutput;
        });
        var second = Handler(TransformationType.ToLower, (row, _) =>
        {
            secondInput = row;
            row.Values["Processed"] = true;
            return row;
        });

        var result = Engine(first, second).Apply(
            source,
            [Rule(1, TransformationType.Trim), Rule(2, TransformationType.ToLower)]);

        Assert.NotSame(source, result);
        Assert.Same(firstOutput, secondInput);
        Assert.Same(secondInput, result);
        Assert.Equal(12, result.SourceRowNumber);
        Assert.Equal("Grace", result.Values["Name"]);
        Assert.Equal(42L, result.Values["Unchanged"]);
        Assert.Equal(true, result.Values["Processed"]);
        Assert.Equal(["Name", "Unchanged", "Processed"], result.Values.Keys);
    }

    [Fact]
    public void Apply_WithNoRulesReturnsOriginalRowWithoutResolvingHandlers()
    {
        var row = Row(3, ("Name", "Ada"));
        var engine = Engine();

        var result = engine.Apply(row, []);

        Assert.Same(row, result);
        Assert.Equal(3, result.SourceRowNumber);
        Assert.Equal("Ada", result.Values["Name"]);
    }

    [Fact]
    public void Apply_ResolvesEveryHandlerBeforeExecutingAnyRule()
    {
        var invocationCount = 0;
        var registered = Handler(TransformationType.Trim, (row, _) =>
        {
            invocationCount++;
            row.Values["Changed"] = true;
            return row;
        });
        var row = Row(4, ("Name", "Ada"));

        var exception = Assert.Throws<KeyNotFoundException>(() => Engine(registered).Apply(
            row,
            [Rule(1, TransformationType.Trim), Rule(2, TransformationType.ToUpper)]));

        Assert.Contains(nameof(TransformationType.ToUpper), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
        Assert.False(row.Values.ContainsKey("Changed"));
    }

    [Fact]
    public void Apply_RejectsDuplicateOrdersBeforeExecutingAnyRule()
    {
        var invocationCount = 0;
        var handler = Handler(TransformationType.Trim, (row, _) =>
        {
            invocationCount++;
            return row;
        });

        var exception = Assert.Throws<InvalidOperationException>(() => Engine(handler).Apply(
            new DataRow(),
            [Rule(5, TransformationType.Trim), Rule(5, TransformationType.Trim)]));

        Assert.Contains("'5'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public void Apply_SortsNegativeZeroAndGappedOrdersNumerically()
    {
        var observedOrders = new List<int>();
        var handler = Handler(TransformationType.Trim, (row, rule) =>
        {
            observedOrders.Add(rule.Order);
            return row;
        });

        Engine(handler).Apply(
            new DataRow(),
            [Rule(100, TransformationType.Trim), Rule(0, TransformationType.Trim), Rule(-5, TransformationType.Trim)]);

        Assert.Equal([-5, 0, 100], observedOrders);
    }

    [Fact]
    public void Apply_RejectsNullArgumentsAndRuleEntries()
    {
        var engine = Engine();

        Assert.Throws<ArgumentNullException>(() => engine.Apply(null!, []));
        Assert.Throws<ArgumentNullException>(() => engine.Apply(new DataRow(), null!));
        Assert.Throws<ArgumentException>(() => engine.Apply(new DataRow(), [null!]));
    }

    [Fact]
    public void Apply_RejectsNullHandlerOutput()
    {
        var handler = Handler(TransformationType.Trim, (_, _) => null!);

        var exception = Assert.Throws<InvalidOperationException>(() => Engine(handler).Apply(
            new DataRow(),
            [Rule(1, TransformationType.Trim)]));

        Assert.Contains(nameof(TransformationType.Trim), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_PropagatesHandlerFailureUnchanged()
    {
        var expected = new FormatException("Handler failure.");
        var handler = Handler(TransformationType.Trim, (_, _) => throw expected);

        var actual = Assert.Throws<FormatException>(() => Engine(handler).Apply(
            new DataRow(),
            [Rule(1, TransformationType.Trim)]));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void Apply_PreservesOrderSemanticsForTrimAndDefaultValueOnWhitespaceOnlyInput()
    {
        var trimThenDefault = Row(1, ("Name", "   "));
        var defaultThenTrim = Row(1, ("Name", "   "));
        var engine = Engine(new TrimTransformationHandler(), new DefaultValueTransformationHandler());

        engine.Apply(
            trimThenDefault,
            [Rule(1, TransformationType.Trim, "Name"), Rule(2, TransformationType.SetDefaultValue, "Name", "Unknown")]);
        engine.Apply(
            defaultThenTrim,
            [Rule(1, TransformationType.SetDefaultValue, "Name", "Unknown"), Rule(2, TransformationType.Trim, "Name")]);

        Assert.Equal("Unknown", trimThenDefault.Values["Name"]);
        Assert.Equal(string.Empty, defaultThenTrim.Values["Name"]);
    }

    private static TransformationEngine Engine(params ITransformationHandler[] handlers) =>
        new(new TransformationHandlerRegistry(handlers));

    private static RecordingHandler Handler(
        TransformationType type,
        Func<DataRow, TransformationRule, DataRow> apply) => new(type, apply);

    private static TransformationRule Rule(int order, TransformationType type) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type
    };

    private static TransformationRule Rule(
        int order,
        TransformationType type,
        string sourceField,
        string? defaultValue = null)
    {
        var rule = new TransformationRule
        {
            Id = Guid.NewGuid(),
            Order = order,
            Type = type,
            SourceField = sourceField
        };

        if (defaultValue is not null)
        {
            rule.Configuration["Value"] = defaultValue;
        }

        return rule;
    }

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

    private sealed class RecordingHandler(
        TransformationType type,
        Func<DataRow, TransformationRule, DataRow> apply) : ITransformationHandler
    {
        public TransformationType Type { get; } = type;

        public DataRow Apply(DataRow row, TransformationRule rule) => apply(row, rule);
    }
}
