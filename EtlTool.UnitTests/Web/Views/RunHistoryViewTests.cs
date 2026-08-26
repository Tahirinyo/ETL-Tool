namespace EtlTool.UnitTests.Web.Views;

public sealed class RunHistoryViewTests
{
    [Fact]
    public void DetailView_UsesSecureRunBasedDownloadRouteWithoutReportPath()
    {
        var view = ReadView("Details.cshtml");

        Assert.Contains("asp-action=\"DownloadErrorReport\"", view);
        Assert.Contains("asp-route-runId=\"@Model.Id\"", view);
        Assert.DoesNotContain("ErrorReportPath", view, StringComparison.Ordinal);
        Assert.DoesNotContain("StoredFilePath", view, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryView_ProvidesPipelineScopedDetailsAndEmptyState()
    {
        var view = ReadView("History.cshtml");

        Assert.Contains("No runs have been recorded for this pipeline.", view);
        Assert.Contains("asp-route-pipelineId=\"@Model.PipelineId\"", view);
        Assert.Contains("asp-route-runId=\"@run.Id\"", view);
    }

    private static string ReadView(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "EtlTool.Web", "Views", "Runs", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not find {fileName} from the test output directory.");
    }
}
