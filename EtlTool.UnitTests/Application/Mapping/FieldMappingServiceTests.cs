using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Domain.Entities;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Mapping;

public sealed class FieldMappingServiceTests
{
    private readonly FieldMappingService _service = new();

    [Fact]
    public void Apply_MapsActiveFieldsInPersistedOrderAndPreservesValues()
    {
        var timestamp = new DateTime(2026, 8, 20, 12, 30, 0, DateTimeKind.Utc);
        var pipeline = Pipeline(
            ["Email", "Name", "Score", "WholeNumber", "Decimal", "CreatedAt", "Empty", "Whitespace", "Missing", "Ignored"],
            Mapping("Score", "score"),
            Mapping("Email", "contactEmail"),
            Mapping("Ignored", "ignored", isIncluded: false),
            Mapping("Name", "Name"),
            Mapping("WholeNumber", "wholeNumber"),
            Mapping("Decimal", "decimal"),
            Mapping("CreatedAt", "createdAt"),
            Mapping("Empty", "empty"),
            Mapping("Whitespace", "whitespace"),
            Mapping("Missing", "missing"));
        var source = Row(
            14,
            ("Email", " ada@example.test "),
            ("Name", "Ada"),
            ("Score", 42.5d),
            ("WholeNumber", 42L),
            ("Decimal", 17.25m),
            ("CreatedAt", timestamp),
            ("Empty", string.Empty),
            ("Whitespace", " \t "),
            ("Missing", null),
            ("Ignored", "do not copy"),
            ("Unmapped", "do not copy"));
        var originalValues = source.Values.ToArray();

        var mapped = _service.Apply(source, _service.Prepare(pipeline));

        Assert.NotSame(source, mapped);
        Assert.NotSame(source.Values, mapped.Values);
        Assert.Equal(14, mapped.SourceRowNumber);
        Assert.Equal(
            ["score", "contactEmail", "Name", "wholeNumber", "decimal", "createdAt", "empty", "whitespace", "missing"],
            mapped.Values.Keys);
        Assert.Equal(42.5d, Assert.IsType<double>(mapped.Values["score"]));
        Assert.Equal(" ada@example.test ", Assert.IsType<string>(mapped.Values["contactEmail"]));
        Assert.Equal("Ada", Assert.IsType<string>(mapped.Values["Name"]));
        Assert.Equal(42L, Assert.IsType<long>(mapped.Values["wholeNumber"]));
        Assert.Equal(17.25m, Assert.IsType<decimal>(mapped.Values["decimal"]));
        Assert.Equal(timestamp, Assert.IsType<DateTime>(mapped.Values["createdAt"]));
        Assert.Equal(string.Empty, Assert.IsType<string>(mapped.Values["empty"]));
        Assert.Equal(" \t ", Assert.IsType<string>(mapped.Values["whitespace"]));
        Assert.Null(mapped.Values["missing"]);
        Assert.Equal(originalValues, source.Values);
    }

    [Fact]
    public void PreparedPlan_IsAnImmutableSnapshotOfActiveMappings()
    {
        var pipeline = Pipeline(
            ["Email"],
            Mapping("Email", "contactEmail"));
        var plan = _service.Prepare(pipeline);
        pipeline.FieldMappings[0].SourceField = "Changed";
        pipeline.FieldMappings[0].TargetField = "changedTarget";
        pipeline.FieldMappings.Clear();
        pipeline.ExpectedSchema.Clear();
        pipeline.ExpectedSchema.Add(new SourceFieldDefinition { Name = "Changed" });

        var mapped = _service.Apply(
            Row(2, ("Email", "ada@example.test")),
            plan);

        Assert.Equal("ada@example.test", mapped.Values["contactEmail"]);
        Assert.False(mapped.Values.ContainsKey("changedTarget"));
    }

    [Fact]
    public void Apply_ProducesDeterministicIndependentRows()
    {
        var plan = _service.Prepare(Pipeline(
            ["Id", "Name"],
            Mapping("Id", "identifier"),
            Mapping("Name", "name")));
        var source = Row(3, ("Id", 7L), ("Name", "Ada"));

        var first = _service.Apply(source, plan);
        var second = _service.Apply(source, plan);

        Assert.NotSame(first, second);
        Assert.Equal(first.Values.Keys, second.Values.Keys);
        Assert.Equal(first.Values, second.Values);
    }

    [Fact]
    public void Apply_UsesExactCaseSensitiveSourceIdentity()
    {
        var plan = _service.Prepare(Pipeline(
            ["Email"],
            Mapping("Email", "contactEmail")));
        var source = Row(8, ("email", "wrong@example.test"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Apply(source, plan));

        Assert.Contains("'Email'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("row 8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_RejectsEmptyExpectedSchema()
    {
        var pipeline = Pipeline([], Mapping("Id", "id"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("expected source schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Prepare_RejectsEmptyExpectedSchemaFieldNames(string fieldName)
    {
        var pipeline = Pipeline([fieldName], Mapping("Id", "id"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("empty field name", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_RejectsDuplicateExpectedSchemaFieldsUsingExactIdentity()
    {
        var pipeline = Pipeline(
            ["Email", "Email"],
            Mapping("Email", "contactEmail"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("duplicate field 'Email'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_TreatsDifferentlyCasedSchemaFieldsAsDistinct()
    {
        var pipeline = Pipeline(
            ["Email", "email"],
            Mapping("Email", "primary"),
            Mapping("email", "secondary"));

        var plan = _service.Prepare(pipeline);
        var mapped = _service.Apply(
            Row(2, ("Email", "first"), ("email", "second")),
            plan);

        Assert.Equal("first", mapped.Values["primary"]);
        Assert.Equal("second", mapped.Values["secondary"]);
    }

    [Fact]
    public void Prepare_RejectsMissingMappedSourceInExpectedSchema()
    {
        var pipeline = Pipeline(
            ["Email"],
            Mapping("email", "contactEmail"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("'email'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expected source schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "target", "source field")]
    [InlineData(" ", "target", "source field")]
    [InlineData("Source", "", "target field")]
    [InlineData("Source", " ", "target field")]
    public void Prepare_RejectsEmptyActiveMappingNames(
        string sourceField,
        string targetField,
        string expectedMessage)
    {
        var pipeline = Pipeline(
            ["Source"],
            Mapping(sourceField, targetField));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prepare_RejectsNullActiveMappingNames(bool nullSourceField)
    {
        var mapping = Mapping("Source", "target");

        if (nullSourceField)
        {
            mapping.SourceField = null!;
        }
        else
        {
            mapping.TargetField = null!;
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(Pipeline(["Source"], mapping)));

        Assert.Contains(
            nullSourceField ? "source field" : "target field",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_IgnoresIncompleteExcludedMapping()
    {
        var pipeline = Pipeline(
            ["Id"],
            Mapping(string.Empty, string.Empty, isIncluded: false),
            Mapping("Id", "id"));

        var mapped = _service.Apply(Row(2, ("Id", 1L)), _service.Prepare(pipeline));

        Assert.Equal(1L, mapped.Values["id"]);
    }

    [Fact]
    public void Apply_PreservesTargetFieldNameExactly()
    {
        var pipeline = Pipeline(
            ["Email"],
            Mapping("Email", " contactEmail "));

        var mapped = _service.Apply(
            Row(2, ("Email", "ada@example.test")),
            _service.Prepare(pipeline));

        Assert.Equal([" contactEmail "], mapped.Values.Keys);
        Assert.Equal("ada@example.test", mapped.Values[" contactEmail "]);
    }

    [Fact]
    public void Prepare_RejectsEmptyMappingCollection()
    {
        var pipeline = Pipeline(["Id"]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("at least one active", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_RejectsEmptyActiveMappingSet()
    {
        var pipeline = Pipeline(
            ["Id"],
            Mapping("Id", "id", isIncluded: false));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("at least one active", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_RejectsDuplicateActiveSourceFields()
    {
        var pipeline = Pipeline(
            ["Email"],
            Mapping("Email", "primaryEmail"),
            Mapping("Email", "secondaryEmail"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("more than one active mapping", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_RejectsDuplicateActiveTargetFields()
    {
        var pipeline = Pipeline(
            ["PrimaryEmail", "SecondaryEmail"],
            Mapping("PrimaryEmail", "email"),
            Mapping("SecondaryEmail", "email"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("more than one active mapping", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_RejectsNullMappingEntry()
    {
        var pipeline = Pipeline(["Id"], Mapping("Id", "id"));
        pipeline.FieldMappings.Add(null!);

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.Prepare(pipeline));

        Assert.Contains("invalid mapping", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PipelineDefinition Pipeline(
        IReadOnlyList<string> schemaFields,
        params FieldMapping[] mappings) => new()
    {
        ExpectedSchema = schemaFields
            .Select(name => new SourceFieldDefinition { Name = name })
            .ToList(),
        FieldMappings = mappings.ToList()
    };

    private static FieldMapping Mapping(
        string source,
        string target,
        bool isIncluded = true) => new()
    {
        SourceField = source,
        TargetField = target,
        IsIncluded = isIncluded
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
