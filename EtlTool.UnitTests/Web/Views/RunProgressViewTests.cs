namespace EtlTool.UnitTests.Web.Views;

public sealed class RunProgressViewTests
{
    private const string ViewRelativePath = "EtlTool.Web/Views/Runs/Progress.cshtml";

    [Fact]
    public void Progress_RendersOnlyPollingFieldsAndHandlesPollingOutcomes()
    {
        var view = ReadView();

        foreach (var target in new[]
                 {
                     "status", "startedAt", "completedAt", "rowProgress", "totalRows", "processedRows",
                     "validRows", "invalidRows", "filteredRows", "deduplicatedRows", "insertedRows", "updatedRows"
                 })
        {
            Assert.Contains($"data-run-field=\"{target}\"", view);
        }

        Assert.Contains("'/Runs/@Model.RunId/Status'", view);
        Assert.Contains("pollingIntervalMilliseconds = 2000", view);
        Assert.Contains("'Completed', 'PartiallyCompleted', 'Failed', 'Interrupted'", view);
        Assert.Contains("if (terminalStatuses.has(run.status)) stopPolling();", view);
        Assert.Contains("response.status === 404", view);
        Assert.Contains("This run was not found or is no longer available.", view);
        Assert.Contains("showError('This run was not found or is no longer available.');\n                        stopPolling();", view);
        Assert.Contains("Progress could not be refreshed. Retrying shortly.", view);
        Assert.Contains("} catch {\n                    showError('Progress could not be refreshed. Retrying shortly.');", view);
        Assert.Contains("const percentage = Math.max(0, Math.min(100", view);
        Assert.Contains("fields.rowProgress.textContent = `${run.processedRows} rows processed`", view);
        Assert.Contains("scheduleNextPoll();", view);

        Assert.DoesNotContain("SystemError", view, StringComparison.Ordinal);
        Assert.DoesNotContain("StoredFilePath", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorReportPath", view, StringComparison.Ordinal);
        Assert.DoesNotContain("PipelineName", view, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginalFileName", view, StringComparison.Ordinal);
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
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not find {ViewRelativePath} from the test output directory.");
    }
}
