namespace EtlTool.UnitTests.Web.Views;

public sealed class PipelineDestinationViewTests
{
    [Fact]
    public void EditView_UsesSavedConnectionsAndAutomaticDiscovery()
    {
        var view = ReadView();

        Assert.Contains("asp-for=\"MongoDbDestinationSavedConnectionId\"", view);
        Assert.Contains("asp-for=\"PostgreSqlDestinationSavedConnectionId\"", view);
        Assert.Contains("/Pipelines/Discovery/PostgreSql/TableMetadata", view);
        Assert.DoesNotContain("Refresh PostgreSQL metadata", view);
    }

    private static string ReadView()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "EtlTool.Web", "Views", "Pipelines", "Edit.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Could not find the pipeline edit view from the test output directory.");
    }
}
