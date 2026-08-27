using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using EtlTool.Application.Processing;
using EtlTool.Application.Reporting;
using EtlTool.Domain.Entities;
using EtlTool.Infrastructure.Reporting;

namespace EtlTool.Infrastructure.Execution;

internal sealed class InvalidRowReportSession : IAsyncDisposable
{
    private readonly IErrorReportWriter _writer;
    private readonly IErrorReportStore _reportStore;
    private readonly EtlRun _run;
    private readonly IReadOnlyList<string> _sourceFields;
    private readonly string _upsertKeyField;
    private readonly CancellationToken _executionToken;
    private readonly object _sync = new();
    private Channel<ReportEnvelope>? _channel;
    private IErrorReportOutput? _output;
    private Task? _writerTask;
    private Task<string?>? _completionTask;
    private Task? _disposeTask;
    private ReportEnvelope? _inFlight;
    private bool _inputCompleted;
    private bool _disposeStarted;
    private bool _published;

    public InvalidRowReportSession(
        IErrorReportWriter writer,
        IErrorReportStore reportStore,
        EtlRun run,
        IReadOnlyList<string> sourceFields,
        string upsertKeyField,
        CancellationToken executionToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(reportStore);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sourceFields);
        ArgumentNullException.ThrowIfNull(upsertKeyField);

        _writer = writer;
        _reportStore = reportStore;
        _run = run;
        _sourceFields = sourceFields.ToArray();
        _upsertKeyField = upsertKeyField;
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

    public Task<string?> CompleteAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return _completionTask ??= CompleteCoreAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task? writerTask;
        IErrorReportOutput? output;
        bool published;

        lock (_sync)
        {
            _disposeStarted = true;
            CompleteInputLocked();
            writerTask = _writerTask;
            output = _output;
            published = _published;
        }

        Exception? writerShutdownException = null;
        try
        {
            if (writerTask is not null)
            {
                await writerTask.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            writerShutdownException = exception;
        }

        Exception? outputCleanupException = null;
        if (output is not null && !published)
        {
            try
            {
                await output.AbortAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outputCleanupException = exception;
            }
        }

        ThrowShutdownFailure(writerShutdownException ?? outputCleanupException);
    }

    private void EnsureStarted()
    {
        lock (_sync)
        {
            if (_completionTask is not null || _disposeStarted)
            {
                throw new InvalidOperationException("The error-report session has already completed.");
            }

            if (_channel is not null)
            {
                return;
            }

            _executionToken.ThrowIfCancellationRequested();
            _output = _reportStore.CreateOutput(_run);
            _channel = Channel.CreateBounded<ReportEnvelope>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _writerTask = RunWriterAsync(_output.Stream, _channel, _executionToken);
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
                _upsertKeyField,
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

    private async Task<string?> CompleteCoreAsync(CancellationToken cancellationToken)
    {
        Channel<ReportEnvelope>? channel;
        Task? writerTask;
        IErrorReportOutput? output;
        lock (_sync)
        {
            channel = _channel;
            writerTask = _writerTask;
            output = _output;
            CompleteInputLocked();
        }

        if (channel is null || writerTask is null || output is null)
        {
            return null;
        }

        try
        {
            await writerTask.ConfigureAwait(false);
            var reference = await output.PublishAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _published = true;
            }

            return reference;
        }
        catch (OperationCanceledException) when (_executionToken.IsCancellationRequested)
        {
            await AbortAfterFailureAsync(output).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await AbortAfterFailureAsync(output).ConfigureAwait(false);
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

        CompleteInput(exception);
    }

    private void CompleteInput(Exception? exception = null)
    {
        lock (_sync)
        {
            CompleteInputLocked(exception);
        }
    }

    private void CompleteInputLocked(Exception? exception = null)
    {
        if (_inputCompleted || _channel is null)
        {
            return;
        }

        _inputCompleted = true;
        _channel.Writer.TryComplete(exception);
    }

    private static async Task AbortAfterFailureAsync(IErrorReportOutput output)
    {
        try
        {
            await output.AbortAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the writer/cancellation failure that ended the reporting session.
        }
    }

    private static void ThrowShutdownFailure(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        if (exception is OperationCanceledException or ErrorReportGenerationException)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        throw new ErrorReportGenerationException(exception);
    }

    private sealed class ReportEnvelope(RowProcessingResult result)
    {
        public RowProcessingResult Result { get; } = result;

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
