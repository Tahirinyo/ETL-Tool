using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Web.Models.Pipelines;

namespace EtlTool.UnitTests.Web.Models;

public sealed class TransformationRuleCardViewModelTests
{
    [Theory]
    [InlineData(TransformationType.Trim, "Trim")]
    [InlineData(TransformationType.ToUpper, "Convert to uppercase")]
    [InlineData(TransformationType.ToLower, "Convert to lowercase")]
    [InlineData(TransformationType.SetDefaultValue, "Set default value")]
    [InlineData(TransformationType.FindAndReplace, "Find and replace")]
    public void FromRule_UsesMeaningfulTypeLabel(TransformationType type, string expectedLabel)
    {
        var card = TransformationRuleCardViewModel.FromRule(new TransformationRule
        {
            Type = type,
            Order = 3,
            SourceField = "name"
        });

        Assert.Equal(expectedLabel, card.TypeLabel);
        Assert.Equal(3, card.Order);
        Assert.Equal("name", card.TargetField);
    }

    [Fact]
    public void FromRule_FormatsDefaultValueSpecialCases()
    {
        var empty = Card(TransformationType.SetDefaultValue, new() { ["Value"] = "" });
        var whitespace = Card(TransformationType.SetDefaultValue, new() { ["Value"] = "  " });
        var missing = Card(TransformationType.SetDefaultValue, new());

        Assert.Equal("Empty string (\"\")", Assert.Single(empty.Configuration).Value);
        Assert.Equal("Whitespace-only value (length: 2)", Assert.Single(whitespace.Configuration).Value);
        Assert.Equal("Configuration unavailable", Assert.Single(missing.Configuration).Value);
        Assert.True(Assert.Single(missing.Configuration).IsWarning);
    }

    [Fact]
    public void FromRule_FormatsFindAndReplaceValuesWithoutTruncation()
    {
        const string longFind = "a very long value that must remain intact";
        var card = Card(
            TransformationType.FindAndReplace,
            new() { ["Find"] = longFind, ["Replace"] = "" });

        Assert.Equal(longFind, card.Configuration[0].Value);
        Assert.Equal("Empty string (\"\")", card.Configuration[1].Value);
    }

    [Fact]
    public void FromRule_HandlesMalformedRuleDataExplicitly()
    {
        var card = TransformationRuleCardViewModel.FromRule(new TransformationRule
        {
            Type = TransformationType.FindAndReplace,
            SourceField = " ",
            Configuration = null!
        });

        Assert.Equal("Unavailable", card.TargetField);
        Assert.All(card.Configuration, item =>
        {
            Assert.Equal("Configuration unavailable", item.Value);
            Assert.True(item.IsWarning);
        });
    }

    private static TransformationRuleCardViewModel Card(
        TransformationType type,
        Dictionary<string, string> configuration) =>
        TransformationRuleCardViewModel.FromRule(new TransformationRule
        {
            Type = type,
            SourceField = "name",
            Configuration = configuration
        });
}
