using EtlTool.Application.Uploads;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.Sources;
using EtlTool.Infrastructure.Uploads;
using EtlTool.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlTool.IntegrationTests.Sources;

public sealed class SourceInspectionCleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"EtlTool-SourceInspectionCleanup-{Guid.NewGuid():N}");

    [Fact]
    public async Task CleanupIoFailure_DoesNotPreventFuturePeriodicCleanupIteration()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new FailFirstOrphanCleanupStorage(localStorage);
        var csv = new CsvFileExtractor();
        var xlsx = new XlsxFileExtractor();
        IUploadValidationService validation = new UploadValidationService(
            storage,
            new UploadValidationOptions
            {
                MaxFileSizeBytes = 1024 * 1024,
                MaxDataRowCount = 100_000
            },
            csv,
            xlsx);
        var inspections = new SourceInspectionService(validation, storage, csv, xlsx);
        var timeProvider = new ManualTimerTimeProvider();
        using var service = new SourceInspectionCleanupService(
            inspections,
            timeProvider,
            NullLogger<SourceInspectionCleanupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, storage.CleanupCallCount);
        timeProvider.Tick();
        await storage.SuccessfulCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.True(storage.CleanupCallCount >= 2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FailFirstOrphanCleanupStorage(IUploadStorage inner) : IUploadStorage
    {
        private int _cleanupCallCount;

        public int CleanupCallCount => Volatile.Read(ref _cleanupCallCount);
        public TaskCompletionSource SuccessfulCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken) =>
            inner.StoreAsync(content, originalFileName, cancellationToken);

        public Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken) =>
            inner.DeleteAsync(upload, cancellationToken);

        public Task DeleteExpiredAsync(
            DateTimeOffset expiresBefore,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _cleanupCallCount) == 1)
            {
                throw new IOException("Simulated temporary cleanup race.");
            }

            SuccessfulCleanup.TrySetResult();
            return inner.DeleteExpiredAsync(expiresBefore, cancellationToken);
        }
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private ManualTimer? _timer;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Volatile.Write(ref _timer, timer);
            return timer;
        }

        public void Tick()
        {
            var timer = Volatile.Read(ref _timer)
                ?? throw new InvalidOperationException("The cleanup timer has not been created.");
            timer.Tick();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Tick()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public void Dispose()
            {
                _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
