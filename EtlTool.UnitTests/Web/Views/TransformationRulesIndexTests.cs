namespace EtlTool.UnitTests.Web.Views;

public sealed class TransformationRulesIndexTests
{
    private const string ViewRelativePath = "EtlTool.Web/Views/TransformationRules/Index.cshtml";

    [Fact]
    public void RuleActions_UseRuleScopedRoutesAndPostDeleteForms()
    {
        var view = ReadView();

        var editActionStart = view.IndexOf("asp-action=\"Edit\"", StringComparison.Ordinal);
        Assert.NotEqual(-1, editActionStart);
        var editActionEnd = view.IndexOf("</a>", editActionStart, StringComparison.Ordinal);
        Assert.NotEqual(-1, editActionEnd);
        var editAction = view[editActionStart..editActionEnd];
        Assert.Contains("asp-route-pipelineId=\"@Model.PipelineId\"", editAction);
        Assert.Contains("asp-route-ruleId=\"@rule.Id\"", editAction);

        var deleteFormStart = view.IndexOf("<form asp-action=\"Delete\"", StringComparison.Ordinal);
        Assert.NotEqual(-1, deleteFormStart);
        var deleteFormEnd = view.IndexOf("</form>", deleteFormStart, StringComparison.Ordinal);
        Assert.NotEqual(-1, deleteFormEnd);
        var deleteForm = view[deleteFormStart..deleteFormEnd];
        Assert.Contains("asp-route-pipelineId=\"@Model.PipelineId\"", deleteForm);
        Assert.Contains("asp-route-ruleId=\"@rule.Id\"", deleteForm);
        Assert.Contains("method=\"post\"", deleteForm);
    }

    private static string ReadView()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ViewRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not find {ViewRelativePath} from the test output directory.");
    }
}
