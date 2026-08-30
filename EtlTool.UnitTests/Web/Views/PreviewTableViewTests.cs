namespace EtlTool.UnitTests.Web.Views;

public sealed class PreviewTableViewTests
{
    private const string ViewRelativePath = "EtlTool.Web/Views/Pipelines/_PreviewTable.cshtml";

    [Fact]
    public void PreviewTable_RendersEncodedColumnsAndCellsWithAnEmptyState()
    {
        var view = ReadView();

        Assert.Contains("@model PreviewTableViewModel", view);
        Assert.Contains("@column", view);
        Assert.Contains("@cell.DisplayValue", view);
        Assert.Contains("@Model.EmptyMessage", view);
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
