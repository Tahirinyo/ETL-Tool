namespace EtlTool.UnitTests.Web.Views;

public sealed class SourceViewTests
{
    [Fact]
    public void SourceView_OffersMongoDbWithSourceSpecificConfigurationFields()
    {
        var view = ReadView();

        Assert.Contains("<option value=\"Csv\">CSV</option>", view);
        Assert.Contains("<option value=\"Xlsx\">Excel (.xlsx)</option>", view);
        Assert.Contains("<option value=\"PostgreSql\">PostgreSQL</option>", view);
        Assert.Contains("<option value=\"MongoDb\">MongoDB</option>", view);
        Assert.Contains("id=\"mongodb-source-fields\"", view);
        Assert.Contains("asp-for=\"MongoDbDatabase\"", view);
        Assert.Contains("asp-for=\"MongoDbCollection\"", view);
        Assert.Contains("asp-for=\"MongoDbSavedConnectionId\"", view);
        Assert.Contains("asp-for=\"PostgreSqlSavedConnectionId\"", view);
        Assert.Contains("/Pipelines/Discovery/MongoDb/Databases", view);
        Assert.Contains("/Pipelines/Discovery/PostgreSql/Databases", view);
        Assert.Contains("id=\"source-metadata-error\"", view);
        Assert.Contains("showMetadataError(data?.error", view);
        Assert.Contains("mongoCollection, mongoDatabase, []", view);
        Assert.DoesNotContain("Configured application MongoDB connection", view);
        Assert.DoesNotContain("Load options", view);
        Assert.DoesNotContain("asp-for=\"DestinationDatabase\"", view);
        Assert.DoesNotContain("asp-for=\"DestinationCollection\"", view);
    }

    private static string ReadView()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "EtlTool.Web",
                "Views",
                "Pipelines",
                "Source.cshtml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Could not find the pipeline source view from the test output directory.");
    }
}
