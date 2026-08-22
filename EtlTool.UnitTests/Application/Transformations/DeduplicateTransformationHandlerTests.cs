using EtlTool.Application.Extraction;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class DeduplicateTransformationHandlerTests
{
    [Fact]
    public void Execution_KeepsFirstAndExplicitlyClassifiesLaterEquivalentRowsWithoutMutation()
    {
        var execution = Execution(Rule(1, "Id"));
        var first = Row(2, ("Id", "A"), ("Other", 1L));
        var second = Row(3, ("Id", "A"), ("Other", 2L));
        var third = Row(4, ("Id", "A"), ("Other", 3L));
        var different = Row(5, ("Id", "B"), ("Other", 2L));
        var secondBefore = second.Values.ToArray();

        var firstResult = execution.Apply(first);
        var secondResult = execution.Apply(second);
        var thirdResult = execution.Apply(third);
        var differentResult = execution.Apply(different);

        Assert.Equal(TransformationResultStatus.Transformed, firstResult.Status);
        Assert.False(firstResult.IsFiltered);
        Assert.False(firstResult.IsDuplicate);
        Assert.Equal(TransformationResultStatus.Duplicate, secondResult.Status);
        Assert.True(secondResult.IsDuplicate);
        Assert.False(secondResult.IsFiltered);
        Assert.Equal(TransformationResultStatus.Duplicate, thirdResult.Status);
        Assert.Equal(TransformationResultStatus.Transformed, differentResult.Status);
        Assert.Same(second, secondResult.Row);
        Assert.Equal(secondBefore, second.Values);
    }

    [Fact]
    public void Execution_UsesCollisionSafeCompositeSelectedFieldsOnly()
    {
        var execution = Execution(Rule(1, "A", "B"));

        Assert.Equal(TransformationResultStatus.Transformed, execution.Apply(
            Row(2, ("A", "A"), ("B", "B|C"), ("Ignored", 1L))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, execution.Apply(
            Row(3, ("A", "A|B"), ("B", "C"), ("Ignored", 1L))).Status);
        Assert.Equal(TransformationResultStatus.Duplicate, execution.Apply(
            Row(4, ("A", "A"), ("B", "B|C"), ("Ignored", 999L))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, execution.Apply(
            Row(5, ("A", "A"), ("B", "different"), ("Ignored", 1L))).Status);
    }

    [Fact]
    public void Execution_PreservesRuntimeTypesAndTypedScalarEquality()
    {
        var execution = Execution(Rule(1, "Value"));
        var timestamp = new DateTime(2026, 8, 22, 10, 30, 0, DateTimeKind.Utc);
        var offsetTimestamp = new DateTimeOffset(2026, 8, 22, 10, 30, 0, TimeSpan.FromHours(3));
        object[] distinctValues = [1L, 1m, "1", true, 1d, timestamp, offsetTimestamp];

        for (var index = 0; index < distinctValues.Length; index++)
        {
            Assert.Equal(TransformationResultStatus.Transformed, execution.Apply(
                Row(index + 2, ("Value", distinctValues[index]))).Status);
        }

        for (var index = 0; index < distinctValues.Length; index++)
        {
            Assert.Equal(TransformationResultStatus.Duplicate, execution.Apply(
                Row(index + 20, ("Value", distinctValues[index]))).Status);
        }
    }

    [Fact]
    public void Execution_UsesOrdinalStringAndExplicitNullSemantics()
    {
        var execution = Execution(Rule(1, "Value"));
        object?[] distinctValues = ["ABC", "abc", " ABC ", "", " ", "null", null];

        for (var index = 0; index < distinctValues.Length; index++)
        {
            Assert.Equal(TransformationResultStatus.Transformed, execution.Apply(
                Row(index + 2, ("Value", distinctValues[index]))).Status);
        }

        Assert.Equal(TransformationResultStatus.Duplicate, execution.Apply(
            Row(20, ("Value", null))).Status);

        var composite = Execution(Rule(1, "A", "B"));
        Assert.Equal(TransformationResultStatus.Transformed, composite.Apply(
            Row(21, ("A", null), ("B", null))).Status);
        Assert.Equal(TransformationResultStatus.Duplicate, composite.Apply(
            Row(22, ("A", null), ("B", null))).Status);

        var partialNull = Execution(Rule(1, "A", "B"));
        Assert.Equal(TransformationResultStatus.Transformed, partialNull.Apply(
            Row(23, ("A", null), ("B", "X"))).Status);
        Assert.Equal(TransformationResultStatus.Duplicate, partialNull.Apply(
            Row(24, ("A", null), ("B", "X"))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, partialNull.Apply(
            Row(25, ("A", null), ("B", "Y"))).Status);
    }

    [Fact]
    public void Execution_UsesOrdinalCaseSensitiveFieldLookup()
    {
        var execution = Execution(Rule(1, "Name"));

        Assert.Equal(TransformationResultStatus.Transformed, execution.Apply(
            Row(2, ("Name", "Ada"), ("name", "first alias"))).Status);
        Assert.Equal(TransformationResultStatus.Duplicate, execution.Apply(
            Row(3, ("Name", "Ada"), ("name", "different alias"))).Status);

        var wrongCase = Execution(Rule(1, "NAME"));
        Assert.Throws<InvalidOperationException>(() => wrongCase.Apply(
            Row(4, ("Name", "Ada"), ("name", "alias"))));
    }

    [Fact]
    public void CreateExecution_RejectsMalformedPersistedConfigurationBeforeRowsRun()
    {
        var invalidRules = new[]
        {
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal)),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["fields"] = "[\"Id\"]" }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = null! }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "not-json" }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "null" }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "[]" }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "[null]" }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "[\" \"]" }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "[\"Id\",\"Id\"]" }),
            RawRule(new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "[1]" })
        };

        foreach (var rule in invalidRules)
        {
            Assert.Throws<InvalidOperationException>(() => Engine().CreateExecution(
                [rule],
                Options()));
        }

        var caseInsensitive = RawRule(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fields"] = "[\"Id\"]"
        });
        Assert.Throws<InvalidOperationException>(() => Engine().CreateExecution(
            [caseInsensitive],
            Options()));
    }

    [Fact]
    public void Execution_MissingOrUnsupportedValuesFailWithoutReservingAKey()
    {
        var missingExecution = Execution(Rule(1, "Id"));
        Assert.Throws<InvalidOperationException>(() => missingExecution.Apply(
            Row(2, ("Other", "A"))));
        Assert.Equal(TransformationResultStatus.Transformed, missingExecution.Apply(
            Row(3, ("Id", "A"))).Status);

        var unsupportedExecution = Execution(Rule(1, "Value"));
        Assert.Throws<InvalidOperationException>(() => unsupportedExecution.Apply(
            Row(4, ("Value", new object()))));
        Assert.Equal(TransformationResultStatus.Transformed, unsupportedExecution.Apply(
            Row(5, ("Value", "A"))).Status);
    }

    [Fact]
    public void Execution_UsesCurrentOrderedValuesAndDuplicateStopsLaterMutation()
    {
        var trimThenDeduplicate = Engine(new TrimTransformationHandler(), new ToLowerTransformationHandler())
            .CreateExecution(
                [FieldRule(3, TransformationType.ToLower, "Name"), Rule(2, "Name"), FieldRule(1, TransformationType.Trim, "Name")],
                Options());

        Assert.Equal(TransformationResultStatus.Transformed, trimThenDeduplicate.Apply(
            Row(2, ("Name", " A "))).Status);
        var duplicate = Row(3, ("Name", "A"));
        Assert.Equal(TransformationResultStatus.Duplicate, trimThenDeduplicate.Apply(duplicate).Status);
        Assert.Equal("A", duplicate.Values["Name"]);

        var deduplicateThenTrim = Engine(new TrimTransformationHandler()).CreateExecution(
            [Rule(1, "Name"), FieldRule(2, TransformationType.Trim, "Name")],
            Options());
        Assert.Equal(TransformationResultStatus.Transformed, deduplicateThenTrim.Apply(
            Row(4, ("Name", " A "))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, deduplicateThenTrim.Apply(
            Row(5, ("Name", "A"))).Status);
    }

    [Fact]
    public void Execution_FilterBeforeOrAfterDeduplicationDoesNotReserveKey()
    {
        var filter = FilterRule(1, "Kind", "skip");
        var filterBefore = Engine(new ConditionalFilterTransformationHandler()).CreateExecution(
            [filter, Rule(2, "Id")],
            Options());

        Assert.Equal(TransformationResultStatus.Filtered, filterBefore.Apply(
            Row(2, ("Kind", "skip"), ("Id", "A"))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, filterBefore.Apply(
            Row(3, ("Kind", "keep"), ("Id", "A"))).Status);

        filter = FilterRule(2, "Kind", "skip");
        var filterAfter = Engine(new ConditionalFilterTransformationHandler()).CreateExecution(
            [Rule(1, "Id"), filter],
            Options());

        Assert.Equal(TransformationResultStatus.Filtered, filterAfter.Apply(
            Row(4, ("Kind", "skip"), ("Id", "A"))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, filterAfter.Apply(
            Row(5, ("Kind", "keep"), ("Id", "A"))).Status);
    }

    [Fact]
    public void Execution_LaterFailureAndLaterDuplicateRollBackEarlierTentativeKeys()
    {
        var throwing = new ThrowingHandler();
        var failureExecution = Engine(throwing).CreateExecution(
            [Rule(1, "Id"), FieldRule(2, TransformationType.ToLower, "Fail")],
            Options());

        Assert.Throws<FormatException>(() => failureExecution.Apply(
            Row(2, ("Id", "A"), ("Fail", true))));
        Assert.Equal(TransformationResultStatus.Transformed, failureExecution.Apply(
            Row(3, ("Id", "A"), ("Fail", false))).Status);
        Assert.Equal(TransformationResultStatus.Duplicate, failureExecution.Apply(
            Row(4, ("Id", "A"), ("Fail", false))).Status);

        var multipleRules = Execution(Rule(1, "A"), Rule(2, "B"));
        Assert.Equal(TransformationResultStatus.Transformed, multipleRules.Apply(
            Row(5, ("A", "1"), ("B", "X"))).Status);
        Assert.Equal(TransformationResultStatus.Duplicate, multipleRules.Apply(
            Row(6, ("A", "2"), ("B", "X"))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, multipleRules.Apply(
            Row(7, ("A", "2"), ("B", "Y"))).Status);
    }

    [Fact]
    public void Executions_AreIsolatedAndLegacyApplyRejectsDeduplication()
    {
        var engine = Engine();
        var rules = new[] { Rule(1, "Id") };
        var firstExecution = engine.CreateExecution(rules, Options());
        var secondExecution = engine.CreateExecution(rules, Options());

        Assert.Equal(TransformationResultStatus.Transformed, firstExecution.Apply(
            Row(2, ("Id", "A"))).Status);
        Assert.Equal(TransformationResultStatus.Duplicate, firstExecution.Apply(
            Row(3, ("Id", "A"))).Status);
        Assert.Equal(TransformationResultStatus.Transformed, secondExecution.Apply(
            Row(4, ("Id", "A"))).Status);
        Assert.Throws<InvalidOperationException>(() => engine.Apply(
            Row(5, ("Id", "A")),
            rules,
            Options()));
    }

    [Fact]
    public async Task Execution_RejectsOverlappingRows()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocking = new BlockingHandler(entered, release);
        var execution = Engine(blocking).CreateExecution(
            [Rule(1, "Id"), FieldRule(2, TransformationType.ToUpper, "Name")],
            Options());

        var firstTask = Task.Run(() => execution.Apply(
            Row(2, ("Id", "A"), ("Name", "Ada"))));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Throws<InvalidOperationException>(() => execution.Apply(
                Row(3, ("Id", "B"), ("Name", "Grace"))));
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(TransformationResultStatus.Transformed, (await firstTask).Status);
        Assert.Equal(TransformationResultStatus.Transformed, execution.Apply(
            Row(4, ("Id", "B"), ("Name", "Grace"))).Status);
    }

    [Fact]
    public void Handler_ReportsTypeAndRejectsContextlessOrNullCalls()
    {
        var handler = new DeduplicateTransformationHandler();
        var row = Row(1, ("Id", "A"));
        var rule = Rule(1, "Id");

        Assert.Equal(TransformationType.Deduplicate, handler.Type);
        Assert.Throws<InvalidOperationException>(() => handler.Apply(row, rule));
        Assert.Throws<ArgumentNullException>(() => handler.Apply(null!, rule));
        Assert.Throws<ArgumentNullException>(() => handler.Apply(row, null!));
    }

    private static TransformationExecution Execution(params TransformationRule[] rules) =>
        Engine().CreateExecution(rules, Options());

    private static TransformationEngine Engine(params ITransformationHandler[] additionalHandlers) =>
        new(new TransformationHandlerRegistry(
            new ITransformationHandler[] { new DeduplicateTransformationHandler() }
                .Concat(additionalHandlers)));

    private static SourceOptions Options() => new() { CultureName = "en-US" };

    private static TransformationRule Rule(int order, params string[] fields) => RawRule(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Fields"] = System.Text.Json.JsonSerializer.Serialize(fields)
        },
        order);

    private static TransformationRule RawRule(
        Dictionary<string, string> configuration,
        int order = 1) => new()
        {
            Id = Guid.NewGuid(),
            Type = TransformationType.Deduplicate,
            Order = order,
            Configuration = configuration
        };

    private static TransformationRule FieldRule(
        int order,
        TransformationType type,
        string field) => new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            Order = order,
            SourceField = field
        };

    private static TransformationRule FilterRule(
        int order,
        string field,
        string value) => new()
        {
            Id = Guid.NewGuid(),
            Type = TransformationType.FilterRow,
            Order = order,
            SourceField = field,
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Operator"] = FilterOperator.Equals.ToString(),
                ["Value"] = value
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

    private sealed class ThrowingHandler : ITransformationHandler
    {
        public TransformationType Type => TransformationType.ToLower;

        public TransformationResult Apply(DataRow row, TransformationRule rule)
        {
            if (rule.SourceField is null
                || !row.Values.TryGetValue(rule.SourceField, out var value))
            {
                throw new InvalidOperationException("The test field is missing.");
            }

            if (value is true)
            {
                throw new FormatException("Later transformation failed.");
            }

            return TransformationResult.Transformed(row);
        }
    }

    private sealed class BlockingHandler(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : ITransformationHandler
    {
        public TransformationType Type => TransformationType.ToUpper;

        public TransformationResult Apply(DataRow row, TransformationRule rule)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The overlap test did not release the handler.");
            }

            return TransformationResult.Transformed(row);
        }
    }
}
