namespace EtlTool.UnitTests.Web.Views;

public sealed class TransformationRuleFormClientValidationTests
{
    private const string FormRelativePath = "EtlTool.Web/Views/TransformationRules/_TransformationRuleForm.cshtml";

    [Theory]
    [InlineData("Trim")]
    [InlineData("ToUpper")]
    [InlineData("ToLower")]
    [InlineData("ConvertToString")]
    [InlineData("ConvertToInteger")]
    [InlineData("ConvertToDecimal")]
    [InlineData("ConvertToDate")]
    [InlineData("SetDefaultValue")]
    [InlineData("FilterRow")]
    [InlineData("FindAndReplace")]
    public void ClientValidation_ListsEverySourceFieldTransformationType(string type)
    {
        var form = ReadForm();

        Assert.Contains($"\"{type}\"", SourceFieldTypesBlock(form));
    }

    [Fact]
    public void ClientValidation_UsesRenderedMappedFieldOptionsAndInitialEditReferences()
    {
        var form = ReadForm();

        Assert.Contains("data-transformation-source-field", form);
        Assert.Contains("data-transformation-deduplication-fields", form);
        Assert.Contains("data-initial-reference=\"@Model.SourceField\"", form);
        Assert.Contains("data-initial-references=\"@JsonSerializer.Serialize(Model.SelectedFields)\"", form);
        Assert.Contains("const availableMappedFields = () => new Set(", form);
        Assert.Contains("Array.from(sourceField.options)", form);
    }

    [Fact]
    public void ClientValidation_ReportsReferenceErrorsAndPreventsInvalidSubmission()
    {
        var form = ReadForm();

        Assert.Contains("Choose a mapped output field.", form);
        Assert.Contains("The selected mapped output field is no longer available.", form);
        Assert.Contains("Choose at least one deduplication field.", form);
        Assert.Contains("One or more selected deduplication fields are no longer available.", form);
        Assert.Contains("form.addEventListener(\"submit\", event =>", form);
        Assert.Contains("if (!validateReferences()) event.preventDefault();", form);
        Assert.Contains("sourceField.addEventListener(\"change\"", form);
        Assert.Contains("deduplicationFields.addEventListener(\"change\"", form);
    }

    [Fact]
    public void ClientValidation_KeepsDeduplicationSeparateFromSourceFieldRules()
    {
        var sourceFieldTypes = SourceFieldTypesBlock(ReadForm());

        Assert.DoesNotContain("\"Deduplicate\"", sourceFieldTypes);
        Assert.DoesNotContain("\"Unspecified\"", sourceFieldTypes);
    }

    private static string SourceFieldTypesBlock(string form)
    {
        const string start = "const sourceFieldTypes = new Set([";
        var startIndex = form.IndexOf(start, StringComparison.Ordinal);
        Assert.NotEqual(-1, startIndex);
        var endIndex = form.IndexOf("]);", startIndex, StringComparison.Ordinal);
        Assert.NotEqual(-1, endIndex);
        return form[startIndex..(endIndex + 2)];
    }

    private static string ReadForm()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, FormRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not find {FormRelativePath} from the test output directory.");
    }
}
