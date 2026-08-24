namespace EtlTool.UnitTests.Web.Views;

public sealed class ValidationRuleFormTests
{
    [Fact]
    public void FormContainsConditionalRangeAndUpsertSections()
    {
        var view = ReadView();
        Assert.Contains("data-validation-type", view);
        Assert.Contains("data-validation-section=\"range\"", view);
        Assert.Contains("data-validation-section=\"upsert\"", view);
        Assert.Contains("UpsertKeyRequired", view);
        Assert.Contains("NumericRange", view);
        Assert.Contains("TextLengthRange", view);
        Assert.Contains("DateRange", view);
    }

    private static string ReadView()
    {
        const string relativePath = "EtlTool.Web/Views/ValidationRules/_ValidationRuleForm.cshtml";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"Could not find {relativePath} from the test output directory.");
    }
}
