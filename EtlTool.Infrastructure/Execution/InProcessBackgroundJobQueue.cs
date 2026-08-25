using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EtlTool.Application.Execution;

namespace EtlTool.Infrastructure.Execution;

public sealed class InProcessBackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<BackgroundJob> _channel;
    private int _admissionClosed;

    public InProcessBackgroundJobQueue(BackgroundJobQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _channel = Channel.CreateBounded<BackgroundJob>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public bool IsAdmissionClosed => Volatile.Read(ref _admissionClosed) != 0;

    public async ValueTask EnqueueAsync(
        BackgroundJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (IsAdmissionClosed)
        {
            throw new BackgroundJobQueueClosedException();
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new BackgroundJobQueueClosedException(exception);
        }
    }

    public async IAsyncEnumerable<BackgroundJob> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var job in _channel.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return job;
        }
    }

    public bool TryRead(out BackgroundJob? job) => _channel.Reader.TryRead(out job);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _admissionClosed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }
}
