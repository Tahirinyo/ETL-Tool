using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoPipelineDefinitionRepositoryFailureTests
{
    [Fact]
    public async Task Repository_RejectsNullPipelinesAndEmptyIdentifiersBeforeIo()
    {
        var repository = CreateUnreachableRepository();
        var emptyIdPipeline = new PipelineDefinition { Id = Guid.Empty };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.AddAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.UpdateAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AddAsync(emptyIdPipeline, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.UpdateAsync(emptyIdPipeline, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetByIdAsync(Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.DeleteAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_UnreachableServerPreservesConnectivityFailure()
    {
        var repository = CreateUnreachableRepository();

        var exception = await Record.ExceptionAsync(
            () => repository.ListAsync(CancellationToken.None));

        Assert.NotNull(exception);
        Assert.IsNotType<DuplicatePipelineDefinitionException>(exception);
        Assert.True(
            exception is TimeoutException or MongoException,
            $"Expected an original MongoDB connectivity failure, but received {exception.GetType().FullName}.");
    }

    [Fact]
    public void MongoDbOptions_RejectsMissingConnectionStringWithoutContainingASecret()
    {
        var options = new MongoDbOptions
        {
            MetadataDatabaseName = "etl_tool_metadata"
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("MongoDb__ConnectionString", exception.Message);
    }

    [Fact]
    public void MongoDbOptions_RejectsMissingMetadataDatabaseName()
    {
        var options = new MongoDbOptions
        {
            ConnectionString = "mongodb://unused",
            MetadataDatabaseName = " "
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static IPipelineDefinitionRepository CreateUnreachableRepository()
    {
        var metadataDatabase = new MongoMetadataDatabase(
            new MongoDbOptions
            {
                ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
                MetadataDatabaseName = $"etl_tool_unreachable_{Guid.NewGuid():N}"
            });

        return new MongoPipelineDefinitionRepository(metadataDatabase);
    }
}
