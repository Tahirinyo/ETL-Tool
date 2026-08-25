using EtlTool.Domain.Entities;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoBsonMappingsTests
{
    [Fact]
    public void EtlRunMapping_SerializesRunAndPipelineIdentifiersAsStandardUuids()
    {
        _ = new MongoMetadataDatabase(new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_bson_mapping_tests"
        });
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            PipelineName = "Customer import",
            OriginalFileName = "customers.csv",
            StoredFilePath = "runs/source.csv"
        };

        var document = run.ToBsonDocument();

        var runId = Assert.IsType<BsonBinaryData>(document["_id"]);
        var pipelineId = Assert.IsType<BsonBinaryData>(document[nameof(EtlRun.PipelineId)]);
        Assert.Equal(BsonBinarySubType.UuidStandard, runId.SubType);
        Assert.Equal(BsonBinarySubType.UuidStandard, pipelineId.SubType);
    }
}
