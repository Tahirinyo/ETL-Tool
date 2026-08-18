using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EtlTool.Infrastructure.Uploads;

namespace EtlTool.IntegrationTests.Uploads;

public sealed class LocalUploadStorageTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"EtlTool-UploadTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task StoreAsync_StreamsContentToGeneratedApplicationPathAndPreservesMetadata()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        var contentBytes = Encoding.UTF8.GetBytes("Id,Name\n1,Çağla");
        using var content = new MemoryStream(contentBytes);

        var result = await storage.StoreAsync(
            content,
            "müşteriler.csv",
            CancellationToken.None);

        Assert.Equal("müşteriler.csv", result.OriginalFileName);
        Assert.Matches(new Regex("^[0-9a-f]{32}\\.upload$", RegexOptions.CultureInvariant), result.StoredFileName);
        Assert.NotEqual(result.OriginalFileName, result.StoredFileName);
        Assert.True(Path.IsPathFullyQualified(result.StoredFilePath));
        Assert.Equal(Path.Combine(rootPath, result.StoredFileName), result.StoredFilePath);
        Assert.Equal(contentBytes.LongLength, result.SizeInBytes);
        Assert.Equal(contentBytes, await File.ReadAllBytesAsync(result.StoredFilePath));
        Assert.True(content.CanRead);
        Assert.Empty(Directory.EnumerateFiles(rootPath, "*.partial"));
    }

    [Theory]
    [InlineData("../../escape.csv", "escape.csv")]
    [InlineData("..\\..\\escape.csv", "escape.csv")]
    [InlineData("/var/tmp/escape.csv", "escape.csv")]
    [InlineData("C:\\temp\\escape.csv", "escape.csv")]
    [InlineData("folder/alt\\unicode-ş.xlsx", "unicode-ş.xlsx")]
    public async Task StoreAsync_UsesOnlyLeafNameAsMetadataAndCannotEscapeRoot(
        string originalFileName,
        string expectedLeafName)
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var content = new MemoryStream([1, 2, 3]);

        var result = await storage.StoreAsync(content, originalFileName, CancellationToken.None);

        Assert.Equal(expectedLeafName, result.OriginalFileName);
        Assert.Equal(
            Path.GetFullPath(rootPath),
            Path.GetDirectoryName(Path.GetFullPath(result.StoredFilePath)));
        Assert.DoesNotContain(expectedLeafName, result.StoredFileName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/")]
    [InlineData("folder/")]
    [InlineData("folder\\")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task StoreAsync_RejectsFilenameWithoutUsableLeafBeforeCreatingStorage(
        string originalFileName)
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var content = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.StoreAsync(content, originalFileName, CancellationToken.None));

        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task StoreAsync_RejectsNullFilenameBeforeCreatingStorage()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var content = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.StoreAsync(content, null!, CancellationToken.None));

        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task StoreAsync_RejectsUnreadableStreamBeforeCreatingStorage()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        var content = new MemoryStream([1]);
        content.Dispose();

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.StoreAsync(content, "file.csv", CancellationToken.None));

        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task StoreAsync_DuplicateOriginalNamesCreateIndependentFiles()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var firstContent = new MemoryStream([1, 2, 3]);
        using var secondContent = new MemoryStream([4, 5, 6]);

        var first = await storage.StoreAsync(firstContent, "same.csv", CancellationToken.None);
        var second = await storage.StoreAsync(secondContent, "same.csv", CancellationToken.None);

        Assert.NotEqual(first.StoredFileName, second.StoredFileName);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(first.StoredFilePath));
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(second.StoredFilePath));
        Assert.Equal(2, Directory.EnumerateFiles(rootPath, "*.upload").Count());
    }

    [Fact]
    public async Task StoreAsync_ConcurrentUploadsUseUniqueNamesWithoutPendingFiles()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);

        var uploads = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(async index =>
                {
                    using var content = new MemoryStream(BitConverter.GetBytes(index));
                    return await storage.StoreAsync(content, "same.csv", CancellationToken.None);
                }));

        Assert.Equal(uploads.Length, uploads.Select(upload => upload.StoredFileName).Distinct().Count());
        Assert.Equal(uploads.Length, Directory.EnumerateFiles(rootPath, "*.upload").Count());
        Assert.Empty(Directory.EnumerateFiles(rootPath, "*.partial"));
    }

    [Fact]
    public async Task StoreAsync_ZeroByteStreamCreatesEmptyUploadAndCreatesMissingDirectoryLazily()
    {
        var rootPath = Path.Combine(_testDirectory, "missing", "uploads");
        var storage = CreateStorage(rootPath);
        using var content = new MemoryStream();
        Assert.False(Directory.Exists(rootPath));

        var result = await storage.StoreAsync(content, "empty.csv", CancellationToken.None);

        Assert.True(Directory.Exists(rootPath));
        Assert.Equal(0, result.SizeInBytes);
        Assert.Equal(0, new FileInfo(result.StoredFilePath).Length);
    }

    [Fact]
    public async Task StoreAsync_LargeNonSeekableStreamIsReadIncrementallyAndRemainsOpen()
    {
        const int contentLength = (1024 * 1024) + 17;
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var content = new GeneratedReadStream(contentLength);

        var result = await storage.StoreAsync(content, "large.csv", CancellationToken.None);

        Assert.Equal(contentLength, result.SizeInBytes);
        Assert.True(content.ReadCallCount > 1);
        Assert.InRange(content.MaximumRequestedCount, 1, 128 * 1024);
        Assert.False(content.IsDisposed);

        await using var storedContent = File.OpenRead(result.StoredFilePath);
        using var expectedContent = new GeneratedReadStream(contentLength);
        Assert.Equal(
            await SHA256.HashDataAsync(expectedContent),
            await SHA256.HashDataAsync(storedContent));
    }

    [Fact]
    public async Task StoreAsync_MidCopyCancellationRemovesPartialFileAndLeavesSourceOpen()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var cancellationSource = new CancellationTokenSource();
        using var content = new PauseOnSecondReadStream();

        var storeTask = storage.StoreAsync(
            content,
            "cancel.csv",
            cancellationSource.Token);
        await content.SecondReadStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storeTask);

        Assert.False(content.IsDisposed);
        Assert.Empty(Directory.EnumerateFiles(rootPath));
    }

    [Fact]
    public async Task StoreAsync_SourceFailureRemovesPartialFileAndPreservesException()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var content = new ThrowAfterFirstReadStream();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => storage.StoreAsync(content, "failure.csv", CancellationToken.None));

        Assert.Same(content.ReadFailure, exception);
        Assert.False(content.IsDisposed);
        Assert.Empty(Directory.EnumerateFiles(rootPath));
    }

    [Fact]
    public async Task StoreAsync_PreCancelledTokenCreatesNoDirectory()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var storage = CreateStorage(rootPath);
        using var cancellationSource = new CancellationTokenSource();
        using var content = new MemoryStream([1]);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.StoreAsync(content, "cancel.csv", cancellationSource.Token));

        Assert.False(Directory.Exists(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task StoreAsync_RootOccupiedByFilePreservesDirectoryCreationFailure()
    {
        Directory.CreateDirectory(_testDirectory);
        var rootPath = Path.Combine(_testDirectory, "root-is-a-file");
        await File.WriteAllTextAsync(rootPath, "occupied");
        var storage = CreateStorage(rootPath);
        using var content = new MemoryStream([1]);

        await Assert.ThrowsAsync<IOException>(
            () => storage.StoreAsync(content, "file.csv", CancellationToken.None));

        Assert.Equal("occupied", await File.ReadAllTextAsync(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public void UploadStorageOptions_ResolveRelativeRootOutsideWebRoot()
    {
        var contentRoot = Path.Combine(_testDirectory, "content");
        var webRoot = Path.Combine(contentRoot, "wwwroot");
        var options = new UploadStorageOptions { RootPath = "App_Data/uploads" };

        var resolved = options.ResolveRootPath(contentRoot, webRoot);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "App_Data/uploads")), resolved);
        options.RootPath = resolved;
        options.Validate();
    }

    [Theory]
    [InlineData("wwwroot")]
    [InlineData("wwwroot/uploads")]
    public void UploadStorageOptions_RejectRootInsideWebRoot(string configuredRoot)
    {
        var contentRoot = Path.Combine(_testDirectory, "content");
        var webRoot = Path.Combine(contentRoot, "wwwroot");
        var options = new UploadStorageOptions { RootPath = configuredRoot };

        var exception = Assert.Throws<InvalidOperationException>(
            () => options.ResolveRootPath(contentRoot, webRoot));

        Assert.Contains("outside the web root", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/uploads")]
    public void UploadStorageOptions_RejectInvalidResolvedRoot(string rootPath)
    {
        var options = new UploadStorageOptions { RootPath = rootPath };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static LocalUploadStorage CreateStorage(string rootPath)
    {
        return new LocalUploadStorage(new UploadStorageOptions { RootPath = Path.GetFullPath(rootPath) });
    }

    private sealed class GeneratedReadStream(long length) : Stream
    {
        private long _remaining = length;

        public int ReadCallCount { get; private set; }

        public int MaximumRequestedCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ReadCallCount++;
            MaximumRequestedCount = Math.Max(MaximumRequestedCount, buffer.Length);

            var count = (int)Math.Min(_remaining, buffer.Length);
            buffer[..count].Fill(0x5A);
            _remaining -= count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class PauseOnSecondReadStream : Stream
    {
        private int _readCount;

        public Task SecondReadStarted => _secondReadStarted.Task;

        public bool IsDisposed { get; private set; }

        private readonly TaskCompletionSource _secondReadStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _readCount++;

            if (_readCount == 1)
            {
                var count = Math.Min(4096, buffer.Length);
                buffer.Span[..count].Fill(0x2A);
                return count;
            }

            _secondReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowAfterFirstReadStream : Stream
    {
        private int _readCount;

        public IOException ReadFailure { get; } = new("Source read failed.");

        public bool IsDisposed { get; private set; }

        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _readCount++;

            if (_readCount == 1)
            {
                var count = Math.Min(4096, buffer.Length);
                buffer.Span[..count].Fill(0x7F);
                return ValueTask.FromResult(count);
            }

            return ValueTask.FromException<int>(ReadFailure);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
