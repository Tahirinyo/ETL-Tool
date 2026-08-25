using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EtlTool.UnitTests")]

namespace EtlTool.Application.Execution;

public sealed class ExecutionCancellationRegistry : IExecutionCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, RegistrationEntry> _registrations = [];
    private readonly Action? _beforeCancellationSignal;

    public ExecutionCancellationRegistry()
    {
    }

    internal ExecutionCancellationRegistry(Action beforeCancellationSignal)
    {
        ArgumentNullException.ThrowIfNull(beforeCancellationSignal);
        _beforeCancellationSignal = beforeCancellationSignal;
    }

    public IExecutionCancellationRegistration Register(Guid runId)
    {
        var entry = new RegistrationEntry(_beforeCancellationSignal);

        if (!_registrations.TryAdd(runId, entry))
        {
            entry.DisposeUnregistered();
            throw new InvalidOperationException(
                $"ETL run '{runId}' already has an active cancellation registration.");
        }

        return new ExecutionCancellationRegistration(this, runId, entry);
    }

    public bool TryRequestCancellation(Guid runId)
    {
        return _registrations.TryGetValue(runId, out var entry)
            && entry.TryRequestCancellation();
    }

    private void Release(Guid runId, RegistrationEntry entry)
    {
        entry.Release(() =>
            ((ICollection<KeyValuePair<Guid, RegistrationEntry>>)_registrations)
                .Remove(new KeyValuePair<Guid, RegistrationEntry>(runId, entry)));
    }

    private sealed class ExecutionCancellationRegistration(
        ExecutionCancellationRegistry registry,
        Guid runId,
        RegistrationEntry entry) : IExecutionCancellationRegistration
    {
        private ExecutionCancellationRegistry? _registry = registry;

        public CancellationToken Token { get; } = entry.Token;

        public void Dispose()
        {
            Interlocked.Exchange(ref _registry, null)?.Release(runId, entry);
        }
    }

    private sealed class RegistrationEntry(Action? beforeCancellationSignal)
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source = new();
        private int _activeCancellationOperations;
        private bool _disposeRequested;

        public CancellationToken Token => _source!.Token;

        public bool TryRequestCancellation()
        {
            CancellationTokenSource source;

            lock (_gate)
            {
                if (_source is null)
                {
                    return false;
                }

                if (_source.IsCancellationRequested)
                {
                    return true;
                }

                _activeCancellationOperations++;
                source = _source;
            }

            try
            {
                beforeCancellationSignal?.Invoke();
                source.Cancel();
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _activeCancellationOperations--;

                    if (_activeCancellationOperations == 0 && _disposeRequested)
                    {
                        DisposeSource();
                    }
                }
            }
        }

        public void Release(Func<bool> removeExactRegistration)
        {
            lock (_gate)
            {
                if (!removeExactRegistration())
                {
                    return;
                }

                if (_activeCancellationOperations > 0)
                {
                    _disposeRequested = true;
                    return;
                }

                DisposeSource();
            }
        }

        public void DisposeUnregistered()
        {
            lock (_gate)
            {
                DisposeSource();
            }
        }

        private void DisposeSource()
        {
            _source?.Dispose();
            _source = null;
            _disposeRequested = false;
        }
    }
}
