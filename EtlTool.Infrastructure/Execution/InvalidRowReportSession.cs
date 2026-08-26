using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EtlTool.Application.Processing;
using EtlTool.Application.Reporting;
using EtlTool.Domain.Entities;

namespace EtlTool.Infrastructure.Execution;

internal sealed class InvalidRowReportSession : IAsyncDisposable
{
    private readonly IErrorReportWriter _writer;
    private readonly IErrorReportOutputStreamFactory _outputFactory;
    private readonly EtlRun _run;
    private readonly IReadOnlyList<string> _sourceFields;
    private readonly CancellationToken _executionToken;
    private readonly object _sync = new();
    private Channel<ReportEnvelope>? _channel;
    private Stream? _output;
    private Task? _writerTask;
    private Task? _completionTask;
    private ReportEnvelope? _inFlight;

    public InvalidRowReportSession(
        IErrorReportWriter writer,
        IErrorReportOutputStreamFactory outputFactory,
        EtlRun run,
        IReadOnlyList<string> sourceFields,
        CancellationToken executionToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(outputFactory);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sourceFields);

        _writer = writer;
        _outputFactory = outputFactory;
        _run = run;
        _sourceFields = sourceFields.ToArray();
        _executionToken = executionToken;
    }

    public bool HasInvalidRows => _channel is not null;

    public async Task ReportAsync(
        RowProcessingResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != RowProcessingStatus.Invalid)
        {
            throw new ArgumentException(
                "Only invalid row-processing results can be written to an error report.",
                nameof(result));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStarted();

            var writerTask = _writerTask!;
            if (writerTask.IsCompleted)
            {
                await writerTask.ConfigureAwait(false);
            }

            var envelope = new ReportEnvelope(result);
            try
            {
                await _channel!.Writer
                    .WriteAsync(envelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                await writerTask.ConfigureAwait(false);
                throw;
            }

            await envelope.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ErrorReportGenerationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ErrorReportGenerationException(exception);
        }
    }

    public Task CompleteAsync()
    {
        lock (_sync)
        {
            return _completionTask ??= CompleteCoreAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // Completion is explicitly awaited on the successful path. During failure unwinding,
            // disposal must preserve the original orchestration/reporting exception.
        }
    }

    private void EnsureStarted()
    {
        lock (_sync)
        {
            if (_completionTask is not null)
            {
                throw new InvalidOperationException("The error-report session has already completed.");
            }

            if (_channel is not null)
            {
                return;
            }

            _executionToken.ThrowIfCancellationRequested();
            _output = _outputFactory.Open(_run);
            _channel = Channel.CreateBounded<ReportEnvelope>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _writerTask = RunWriterAsync(_output, _channel, _executionToken);
        }
    }

    private async Task RunWriterAsync(
        Stream output,
        Channel<ReportEnvelope> channel,
        CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteAsync(
                output,
                _run.Id,
                _sourceFields,
                ReadRowsAsync(channel.Reader, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            FailOutstanding(channel, exception);
            throw;
        }
    }

    private async IAsyncEnumerable<RowProcessingResult> ReadRowsAsync(
        ChannelReader<ReportEnvelope> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            lock (_sync)
            {
                _inFlight = envelope;
            }

            yield return envelope.Result;

            envelope.Completion.TrySetResult();
            lock (_sync)
            {
                if (ReferenceEquals(_inFlight, envelope))
                {
                    _inFlight = null;
                }
            }
        }
    }

    private async Task CompleteCoreAsync()
    {
        Channel<ReportEnvelope>? channel;
        Task? writerTask;
        Stream? output;
        lock (_sync)
        {
            channel = _channel;
            writerTask = _writerTask;
            output = _output;
        }

        if (channel is null || writerTask is null || output is null)
        {
            return;
        }

        try
        {
            channel.Writer.TryComplete();
            await writerTask.ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_executionToken.IsCancellationRequested)
        {
            await DisposeAfterFailureAsync(output).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DisposeAfterFailureAsync(output).ConfigureAwait(false);
            throw exception is ErrorReportGenerationException
                ? exception
                : new ErrorReportGenerationException(exception);
        }
    }

    private void FailOutstanding(
        Channel<ReportEnvelope> channel,
        Exception exception)
    {
        ReportEnvelope? inFlight;
        lock (_sync)
        {
            inFlight = _inFlight;
            _inFlight = null;
        }

        inFlight?.Completion.TrySetException(exception);
        while (channel.Reader.TryRead(out var queued))
        {
            queued.Completion.TrySetException(exception);
        }

        channel.Writer.TryComplete(exception);
    }

    private static async Task DisposeAfterFailureAsync(Stream output)
    {
        try
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the writer/cancellation failure that ended the reporting session.
        }
    }

    private sealed class ReportEnvelope(RowProcessingResult result)
    {
        public RowProcessingResult Result { get; } = result;

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
