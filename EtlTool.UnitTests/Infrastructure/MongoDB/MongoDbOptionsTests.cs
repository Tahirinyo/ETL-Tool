using EtlTool.Infrastructure.MongoDB;

namespace EtlTool.UnitTests.Infrastructure.MongoDB;

public sealed class MongoDbOptionsTests
{
    [Fact]
    public void SourceSchemaSampleDocumentLimit_DefaultsToOneHundred()
    {
        var options = ValidOptions();

        options.Validate();

        Assert.Equal(100, options.SourceSchemaSampleDocumentLimit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Validate_AcceptsSupportedSourceSchemaSampleDocumentLimit(int limit)
    {
        var options = ValidOptions();
        options.SourceSchemaSampleDocumentLimit = limit;

        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Validate_RejectsUnsupportedSourceSchemaSampleDocumentLimit(int limit)
    {
        var options = ValidOptions();
        options.SourceSchemaSampleDocumentLimit = limit;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(nameof(MongoDbOptions.SourceSchemaSampleDocumentLimit), exception.Message);
        Assert.DoesNotContain(options.ConnectionString, exception.Message, StringComparison.Ordinal);
    }

    private static MongoDbOptions ValidOptions() => new()
    {
        ConnectionString = "mongodb://127.0.0.1:1",
        MetadataDatabaseName = "etl_tool_metadata"
    };
}
