using EtlTool.Application.Sources;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Sources;

public sealed class SourceSchemaComparisonServiceTests
{
    private readonly SourceSchemaComparisonService _service = new();

    [Fact]
    public void Compare_UnchangedAndReorderedSchemasHaveNoDifferences()
    {
        SourceFieldDefinition[] saved = [Field("Id", SourceFieldType.Integer), Field("Email", SourceFieldType.String)];
        SourceFieldDefinition[] inspected = [Field("Email", SourceFieldType.String), Field("Id", SourceFieldType.Integer)];

        var result = _service.Compare(saved, inspected, [Mapping("Id", "id"), Mapping("Email", "email")]);

        Assert.False(result.HasDifferences);
        Assert.False(result.HasUnresolvedMappings);
    }

    [Fact]
    public void Compare_UsesExactCaseSensitiveIdentityAndPreservesConfiguredOrder()
    {
        var result = _service.Compare(
            [Field("Email", SourceFieldType.String), Field("Id", SourceFieldType.Integer)],
            [Field("email", SourceFieldType.String), Field("Name", SourceFieldType.String)],
            [Mapping("Id", "id", false), Mapping("Email", "email")]);

        Assert.Equal(["Email", "Id"], result.MissingFields.Select(field => field.Name));
        Assert.Equal(["email", "Name"], result.NewFields.Select(field => field.Name));
        Assert.Collection(result.UnresolvedMappings,
            mapping =>
            {
                Assert.Equal("Id", mapping.SourceField);
                Assert.Equal("id", mapping.TargetField);
                Assert.False(mapping.IsIncluded);
            },
            mapping => Assert.Equal("Email", mapping.SourceField));
    }

    [Fact]
    public void Compare_TypeOnlyChangeIsInformationalAndMappingsRemainResolvable()
    {
        var result = _service.Compare(
            [Field("Amount", SourceFieldType.Integer)],
            [Field("Amount", SourceFieldType.Decimal)],
            [Mapping("Amount", "amount")]);

        var change = Assert.Single(result.TypeChanges);
        Assert.Equal("Amount", change.FieldName);
        Assert.Equal(SourceFieldType.Integer, change.SavedType);
        Assert.Equal(SourceFieldType.Decimal, change.InspectedType);
        Assert.Empty(result.UnresolvedMappings);
        Assert.True(result.HasDifferences);
        Assert.False(result.HasUnresolvedMappings);
    }

    [Fact]
    public void Compare_ReportsMixedDifferencesInDeterministicOrders()
    {
        var result = _service.Compare(
            [
                Field("First", SourceFieldType.String),
                Field("Changed", SourceFieldType.Integer),
                Field("MissingA", SourceFieldType.String),
                Field("MissingB", SourceFieldType.String)
            ],
            [
                Field("NewA", SourceFieldType.String),
                Field("Changed", SourceFieldType.Decimal),
                Field("First", SourceFieldType.String),
                Field("NewB", SourceFieldType.String)
            ],
            [Mapping("MissingB", "b", false), Mapping("First", "first"), Mapping("MissingA", "a")]);

        Assert.Equal(["MissingA", "MissingB"], result.MissingFields.Select(field => field.Name));
        Assert.Equal(["NewA", "NewB"], result.NewFields.Select(field => field.Name));
        Assert.Equal("Changed", Assert.Single(result.TypeChanges).FieldName);
        Assert.Equal(["MissingB", "MissingA"], result.UnresolvedMappings.Select(mapping => mapping.SourceField));
        Assert.False(result.UnresolvedMappings[0].IsIncluded);
    }

    private static SourceFieldDefinition Field(string name, SourceFieldType type) => new()
    {
        Name = name,
        DataType = type
    };

    private static FieldMapping Mapping(string source, string target, bool included = true) => new()
    {
        SourceField = source,
        TargetField = target,
        IsIncluded = included
    };
}
