namespace EtlTool.UnitTests.Web.Views;

public sealed class PreviewErrorTableViewTests
{
    private const string ViewRelativePath = "EtlTool.Web/Views/Pipelines/_PreviewErrors.cshtml";

    [Fact]
    public void PreviewErrors_RendersStructuredValuesWithRowLevelAndEmptyStates()
    {
        var view = ReadView();

        Assert.Contains("@model PreviewErrorTableViewModel", view);
        Assert.Contains("@error.SourceRowNumber", view);
        Assert.Contains("@(error.Field ?? \"Row-level\")", view);
        Assert.Contains("@error.Stage", view);
        Assert.Contains("@error.Message", view);
        Assert.Contains("No row-level errors are available", view);
        Assert.DoesNotContain("Html.Raw", view, StringComparison.Ordinal);
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
