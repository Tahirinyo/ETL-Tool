namespace EtlTool.UnitTests.Web.Views;

public sealed class PipelinePreviewViewTests
{
    private const string ViewRelativePath = "EtlTool.Web/Views/Pipelines/Preview.cshtml";

    [Fact]
    public void Preview_ComposesCountersExistingPartialsAndSafeFailureStates()
    {
        var view = ReadView();

        Assert.Contains("@Model.PreviewedRowCount", view);
        Assert.Contains("Previewed", view);
        Assert.Contains("@Model.ValidRowCount", view);
        Assert.Contains("@Model.InvalidRowCount", view);
        Assert.Contains("@Model.FilteredRowCount", view);
        Assert.Contains("@Model.DuplicateRowCount", view);
        Assert.Contains("model=\"Model.ValidRows!\"", view);
        Assert.Contains("_PreviewTable", view);
        Assert.Contains("_PreviewErrors", view);
        Assert.Contains("Preview results are based on the first 100 source rows.", view);
        Assert.Contains("Valid rows", view);
        Assert.Contains("Rows after transformation", view);
        Assert.Contains("may still fail validation", view);
        Assert.Contains("@problem.Component", view);
        Assert.Contains("@problem.Message", view);
        Assert.Contains("@Model.FailureMessage", view);
        Assert.Contains("Upload source again", view);
        Assert.DoesNotContain("Html.Raw", view, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceReference", view, StringComparison.Ordinal);
        Assert.DoesNotContain("StoredFile", view, StringComparison.Ordinal);
    }

    private static string ReadView()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                ViewRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Could not find {ViewRelativePath} from the test output directory.");
    }
}
