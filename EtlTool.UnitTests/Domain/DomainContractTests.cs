using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.UnitTests.Domain;

public sealed class DomainContractTests
{
    [Fact]
    public void EnumContracts_ExposeApprovedNamesAndStableValues()
    {
        Assert.Equal(
            ["Unspecified:0", "Csv:1", "Xlsx:2"],
            GetEnumContract<SourceType>());
        Assert.Equal(
            ["Comma:1", "Semicolon:2", "Tab:3"],
            GetEnumContract<CsvDelimiter>());
        Assert.Equal(
            ["Unknown:0", "String:1", "Integer:2", "Decimal:3", "Date:4"],
            GetEnumContract<SourceFieldType>());
        Assert.Equal(
            [
                "Unspecified:0",
                "Trim:1",
                "ToUpper:2",
                "ToLower:3",
                "ConvertToString:4",
                "ConvertToInteger:5",
                "ConvertToDecimal:6",
                "ConvertToDate:7",
                "SetDefaultValue:8",
                "FilterRow:9",
                "FindAndReplace:10",
                "Deduplicate:11"
            ],
            GetEnumContract<TransformationType>());
        Assert.Equal(
            [
                "Unspecified:0",
                "Required:1",
                "EmailFormat:2",
                "NumericRange:3",
                "TextLengthRange:4",
                "DateRange:5",
                "UpsertKeyRequired:6"
            ],
            GetEnumContract<ValidationType>());
        Assert.Equal(
            [
                "Queued:0",
                "Running:1",
                "Completed:2",
                "PartiallyCompleted:3",
                "Failed:4",
                "Interrupted:5"
            ],
            GetEnumContract<EtlRunStatus>());
    }

    [Fact]
    public void PipelineDefinition_InitializesIndependentEditableState()
    {
        var first = new PipelineDefinition();
        var second = new PipelineDefinition();

        Assert.Null(first.Description);
        Assert.Empty(first.ExpectedSchema);
        Assert.Empty(first.FieldMappings);
        Assert.Empty(first.TransformationRules);
        Assert.Empty(first.ValidationRules);
        Assert.True(first.SourceOptions.FirstRowIsHeader);

        Assert.NotSame(first.SourceOptions, second.SourceOptions);
        Assert.NotSame(first.ExpectedSchema, second.ExpectedSchema);
        Assert.NotSame(first.FieldMappings, second.FieldMappings);
        Assert.NotSame(first.TransformationRules, second.TransformationRules);
        Assert.NotSame(first.ValidationRules, second.ValidationRules);
    }

    [Fact]
    public void RuleConfigurations_AreIndependentAndUseOrdinalKeys()
    {
        var firstTransformation = new TransformationRule();
        var secondTransformation = new TransformationRule();
        var firstValidation = new ValidationRule();
        var secondValidation = new ValidationRule();

        firstTransformation.Configuration["Value"] = "first";
        firstTransformation.Configuration["value"] = "second";
        firstValidation.Configuration["Value"] = "first";
        firstValidation.Configuration["value"] = "second";

        Assert.Equal(2, firstTransformation.Configuration.Count);
        Assert.Equal(2, firstValidation.Configuration.Count);
        Assert.Equal(StringComparer.Ordinal, firstTransformation.Configuration.Comparer);
        Assert.Equal(StringComparer.Ordinal, firstValidation.Configuration.Comparer);
        Assert.NotSame(firstTransformation.Configuration, secondTransformation.Configuration);
        Assert.NotSame(firstValidation.Configuration, secondValidation.Configuration);
        Assert.Empty(secondValidation.Configuration);
    }

    [Fact]
    public void EtlRun_DefaultsToQueuedWithOpenLifecycleAndNoErrors()
    {
        var run = new EtlRun();

        Assert.Equal(EtlRunStatus.Queued, run.Status);
        Assert.Null(run.StartedAt);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.SystemError);
        Assert.Null(run.ErrorReportPath);
    }

    private static string[] GetEnumContract<TEnum>()
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()
            .Select(value => $"{value}:{Convert.ToInt32(value)}")
            .ToArray();
    }
}
