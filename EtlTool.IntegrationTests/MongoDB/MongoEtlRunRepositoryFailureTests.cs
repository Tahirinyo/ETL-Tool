using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoEtlRunRepositoryFailureTests
{
    [Fact]
    public async Task Repository_RejectsInvalidArgumentsBeforeIo()
    {
        var repository = CreateUnreachableRepository();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.AddAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AddAsync(new EtlRun(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetByIdAsync(Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.TryStartAsync(Guid.Empty, DateTimeOffset.UtcNow, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.TryUpdateProgressAsync(
                Guid.NewGuid(),
                null!,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.TryMarkTerminalAsync(
                Guid.NewGuid(),
                EtlRunStatus.Running,
                DateTimeOffset.UtcNow,
                null,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.TryMarkTerminalAsync(
                Guid.NewGuid(),
                (EtlRunStatus)99,
                DateTimeOffset.UtcNow,
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task GetByIdAsync_PropagatesCancellation()
    {
        var repository = CreateUnreachableRepository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetByIdAsync(Guid.NewGuid(), cancellation.Token));
    }

    [Fact]
    public async Task GetByIdAsync_UnreachableServerPreservesConnectivityFailure()
    {
        var repository = CreateUnreachableRepository();

        var exception = await Record.ExceptionAsync(
            () => repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.NotNull(exception);
        Assert.True(
            exception is TimeoutException or MongoException,
            $"Expected an original MongoDB connectivity failure, but received {exception.GetType().FullName}.");
    }

    private static IEtlRunRepository CreateUnreachableRepository()
    {
        var metadataDatabase = new MongoMetadataDatabase(
            new MongoDbOptions
            {
                ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
                MetadataDatabaseName = $"etl_tool_unreachable_{Guid.NewGuid():N}"
            });

        return new MongoEtlRunRepository(metadataDatabase);
    }
}
