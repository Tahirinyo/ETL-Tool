using EtlTool.Application.Execution;
using EtlTool.Infrastructure.Execution;

namespace EtlTool.IntegrationTests.Execution;

public sealed class InProcessBackgroundJobQueueTests
{
    [Fact]
    public void Options_DefaultToOneHundredAndRejectNonPositiveCapacity()
    {
        var options = new BackgroundJobQueueOptions();

        Assert.Equal(100, options.Capacity);
        options.Validate();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new BackgroundJobQueueOptions { Capacity = 0 }.Validate());
        Assert.Contains("BackgroundJobQueue:Capacity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnqueueAsync_WhenFullWaitsAndHonorsAdmissionCancellation()
    {
        var queue = CreateQueue(capacity: 1);
        var admitted = new BackgroundJob(Guid.NewGuid());
        await queue.EnqueueAsync(admitted, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waitingAdmission = queue
            .EnqueueAsync(new BackgroundJob(Guid.NewGuid()), cancellation.Token)
            .AsTask();

        Assert.False(waitingAdmission.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingAdmission);
        queue.Complete();

        var retained = await ReadAllAsync(queue);
        Assert.Equal([admitted], retained);
    }

    [Fact]
    public async Task Queue_RetainsMultipleJobsInFifoOrder()
    {
        var queue = CreateQueue(capacity: 3);
        var jobs = new[]
        {
            new BackgroundJob(Guid.NewGuid()),
            new BackgroundJob(Guid.NewGuid()),
            new BackgroundJob(Guid.NewGuid())
        };

        foreach (var job in jobs)
        {
            await queue.EnqueueAsync(job, CancellationToken.None);
        }

        queue.Complete();

        Assert.Equal(jobs, await ReadAllAsync(queue));
    }

    [Fact]
    public async Task EnqueueAsync_AfterCompletionThrowsClearQueueClosedFailure()
    {
        var queue = CreateQueue(capacity: 1);
        queue.Complete();

        var exception = await Assert.ThrowsAsync<BackgroundJobQueueClosedException>(() =>
            queue.EnqueueAsync(new BackgroundJob(Guid.NewGuid()), CancellationToken.None).AsTask());

        Assert.Contains("shutting down", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackgroundJob_RejectsEmptyRunIdentifier()
    {
        var exception = Assert.Throws<ArgumentException>(() => new BackgroundJob(Guid.Empty));

        Assert.Equal("runId", exception.ParamName);
    }

    private static InProcessBackgroundJobQueue CreateQueue(int capacity) =>
        new(new BackgroundJobQueueOptions { Capacity = capacity });

    private static async Task<IReadOnlyList<BackgroundJob>> ReadAllAsync(
        InProcessBackgroundJobQueue queue)
    {
        var jobs = new List<BackgroundJob>();
        await foreach (var job in queue.ReadAllAsync(CancellationToken.None))
        {
            jobs.Add(job);
        }

        return jobs;
    }
}
