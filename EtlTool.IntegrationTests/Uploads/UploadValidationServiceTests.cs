using System.Text;
using EtlTool.Application.Uploads;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.Uploads;
using static EtlTool.IntegrationTests.Extraction.OpenXmlWorkbookFixture;

namespace EtlTool.IntegrationTests.Uploads;

[Collection(UploadValidationTestCollection.Name)]
public sealed class UploadValidationServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"EtlTool-UploadValidationTests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("source.csv")]
    [InlineData("source.CSV")]
    [InlineData("archive.exe.csv")]
    [InlineData("folder/alt\\mÃ¼ÅŸteriler.CsV")]
    public async Task StoreValidatedAsync_AcceptsCsvFinalExtensionAndPreservesLeafMetadata(
        string originalFileName)
    {
        var (service, storage, _) = CreateService();
        using var content = Csv("Id,Name\n1,Ada");

        var result = await service.StoreValidatedAsync(
            content,
            originalFileName,
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(
            originalFileName.Replace('\\', '/').Split('/').Last(),
            result.Upload!.OriginalFileName);
        Assert.True(content.CanRead);
        await storage.DeleteAsync(result.Upload, CancellationToken.None);
    }

    [Fact]
    public async Task StoreValidatedAsync_AcceptsUppercaseXlsxExtension()
    {
        var (service, storage, _) = CreateService();
        using var content = Create(
            Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));

        var result = await service.StoreValidatedAsync(
            content,
            "SOURCE.XLSX",
            SourceType.Xlsx,
            XlsxOptions("Data"),
            CancellationToken.None);

        Assert.True(result.IsValid);
        await storage.DeleteAsync(result.Upload!, CancellationToken.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("folder/")]
    [InlineData("folder\\")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task StoreValidatedAsync_RejectsInvalidLeafBeforeStorage(string originalFileName)
    {
        var (service, _, rootPath) = CreateService();
        using var content = Csv("Id\n1");

        var result = await service.StoreValidatedAsync(
            content,
            originalFileName,
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.InvalidFileName);
        Assert.False(Directory.Exists(rootPath));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("source.")]
    [InlineData("source.xls")]
    [InlineData("source.txt")]
    [InlineData("source.csv.exe")]
    [InlineData("source.csv ")]
    public async Task StoreValidatedAsync_RejectsUnsupportedFinalExtensionBeforeStorage(
        string originalFileName)
    {
        var (service, _, rootPath) = CreateService();
        using var content = Csv("Id\n1");

        var result = await service.StoreValidatedAsync(
            content,
            originalFileName,
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.UnsupportedExtension);
        Assert.False(Directory.Exists(rootPath));
    }

    [Theory]
    [InlineData("source.csv", SourceType.Xlsx, UploadValidationFailureCode.SourceTypeMismatch)]
    [InlineData("source.xlsx", SourceType.Csv, UploadValidationFailureCode.SourceTypeMismatch)]
    [InlineData("source.csv", SourceType.Unspecified, UploadValidationFailureCode.UnsupportedSourceType)]
    [InlineData("source.csv", (SourceType)999, UploadValidationFailureCode.UnsupportedSourceType)]
    public async Task StoreValidatedAsync_RejectsUnsupportedOrMismatchedSourceTypeBeforeStorage(
        string originalFileName,
        SourceType sourceType,
        UploadValidationFailureCode expectedCode)
    {
        var (service, _, rootPath) = CreateService();
        using var content = Csv("Id\n1");

        var result = await service.StoreValidatedAsync(
            content,
            originalFileName,
            sourceType,
            new SourceOptions { WorksheetName = "Data" },
            CancellationToken.None);

        AssertRejected(result, expectedCode);
        Assert.False(Directory.Exists(rootPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task StoreValidatedAsync_RequiresXlsxWorksheetBeforeStorage(string? worksheetName)
    {
        var (service, _, rootPath) = CreateService();
        using var content = Csv("not read");

        var result = await service.StoreValidatedAsync(
            content,
            "source.xlsx",
            SourceType.Xlsx,
            XlsxOptions(worksheetName),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.WorksheetRequired);
        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task StoreValidatedAsync_AcceptsFileExactlyAtConfiguredSize()
    {
        var bytes = Encoding.UTF8.GetBytes("Id\n1");
        var (service, storage, _) = CreateService(maxFileSizeBytes: bytes.Length);
        using var content = new MemoryStream(bytes);

        var result = await service.StoreValidatedAsync(
            content,
            "source.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(bytes.Length, result.Upload!.SizeInBytes);
        await storage.DeleteAsync(result.Upload, CancellationToken.None);
    }

    [Fact]
    public async Task StoreValidatedAsync_RejectsKnownOversizeBeforeCreatingStorage()
    {
        var (service, _, rootPath) = CreateService(maxFileSizeBytes: 3);
        using var content = Csv("Id\n1");

        var result = await service.StoreValidatedAsync(
            content,
            "source.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.FileTooLarge);
        Assert.False(Directory.Exists(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task StoreValidatedAsync_RejectsNonSeekableOversizeDuringCopyWithoutArtifacts()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var (service, _, _) = CreateService(rootPath, maxFileSizeBytes: 3);
        using var content = new NonSeekableReadStream(Encoding.UTF8.GetBytes("Id\n1"));

        var result = await service.StoreValidatedAsync(
            content,
            "source.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.FileTooLarge);
        Assert.False(content.IsDisposed);
        Assert.True(Directory.Exists(rootPath));
        Assert.Empty(Directory.EnumerateFiles(rootPath));
    }

    [Fact]
    public async Task StoreValidatedAsync_RejectsSeekableAndNonSeekableZeroByteContent()
    {
        var seekableRoot = Path.Combine(_testDirectory, "seekable");
        var (seekableService, _, _) = CreateService(seekableRoot);
        using var seekable = new MemoryStream();

        var seekableResult = await seekableService.StoreValidatedAsync(
            seekable,
            "empty.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(seekableResult, UploadValidationFailureCode.EmptyFile);
        Assert.False(Directory.Exists(seekableRoot));

        var nonSeekableRoot = Path.Combine(_testDirectory, "non-seekable");
        var (nonSeekableService, _, _) = CreateService(nonSeekableRoot);
        using var nonSeekable = new NonSeekableReadStream([]);

        var nonSeekableResult = await nonSeekableService.StoreValidatedAsync(
            nonSeekable,
            "empty.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(nonSeekableResult, UploadValidationFailureCode.EmptyFile);
        Assert.False(nonSeekable.IsDisposed);
        Assert.Empty(Directory.EnumerateFiles(nonSeekableRoot));
    }

    [Fact]
    public async Task StoreValidatedAsync_UsesLogicalCsvRowsAndExcludesHeader()
    {
        var (service, storage, _) = CreateService(maxDataRowCount: 2);
        using var content = Csv("Id,Notes\n1,\"first\nsecond\"\n2,plain");

        var result = await service.StoreValidatedAsync(
            content,
            "source.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        Assert.True(result.IsValid);
        await storage.DeleteAsync(result.Upload!, CancellationToken.None);
    }

    [Fact]
    public async Task StoreValidatedAsync_StopsAtFirstCsvRowOverLimitAndCleansUpload()
    {
        var (service, _, rootPath) = CreateService(maxDataRowCount: 2);
        using var content = Csv("A\n1\n2\n3\n4,too-wide");

        var result = await service.StoreValidatedAsync(
            content,
            "source.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.RowLimitExceeded);
        Assert.Empty(Directory.EnumerateFiles(rootPath));
    }

    [Fact]
    public async Task StoreValidatedAsync_AcceptsHeaderOnlyCsv()
    {
        var (service, storage, _) = CreateService(maxDataRowCount: 1);
        using var content = Csv("Id,Name");

        var result = await service.StoreValidatedAsync(
            content,
            "source.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        Assert.True(result.IsValid);
        await storage.DeleteAsync(result.Upload!, CancellationToken.None);
    }

    [Fact]
    public async Task StoreValidatedAsync_UsesOnlySelectedWorksheetAndExtractorBlankRowSemantics()
    {
        var (service, storage, _) = CreateService(maxDataRowCount: 1);
        using var content = Create(
            Sheet(
                "TooMany",
                Row(Text(1, "Id")),
                Row(Number(1, 1)),
                Row(Number(1, 2))),
            Sheet(
                "Selected",
                Row(Text(1, "Id")),
                Row(Blank(1)),
                Row(SharedEmptyText(1))));

        var result = await service.StoreValidatedAsync(
            content,
            "source.xlsx",
            SourceType.Xlsx,
            XlsxOptions("Selected"),
            CancellationToken.None);

        Assert.True(result.IsValid);
        await storage.DeleteAsync(result.Upload!, CancellationToken.None);
    }

    [Theory]
    [InlineData("source.csv", SourceType.Csv)]
    [InlineData("source.xlsx", SourceType.Xlsx)]
    public async Task StoreValidatedAsync_ReturnsInvalidSourceAndCleansMalformedFile(
        string originalFileName,
        SourceType sourceType)
    {
        var (service, _, rootPath) = CreateService();
        using var content = sourceType == SourceType.Csv
            ? Csv("A,B\n1,bad\"value")
            : new MemoryStream(Encoding.UTF8.GetBytes("not-an-xlsx-workbook"));
        var options = sourceType == SourceType.Xlsx
            ? XlsxOptions("Data")
            : new SourceOptions();

        var result = await service.StoreValidatedAsync(
            content,
            originalFileName,
            sourceType,
            options,
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.InvalidSourceFile);
        Assert.Empty(Directory.EnumerateFiles(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task StoreValidatedAsync_CancellationAfterStorageDeletesFinalUpload()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var localStorage = CreateStorage(rootPath);
        using var cancellationSource = new CancellationTokenSource();
        var storage = new CancelAfterStoreStorage(localStorage, cancellationSource);
        var service = CreateService(storage);
        using var content = Csv("Id\n1\n2");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StoreValidatedAsync(
                content,
                "source.csv",
                SourceType.Csv,
                new SourceOptions(),
                cancellationSource.Token));

        Assert.True(storage.DeleteCalled);
        Assert.Empty(Directory.EnumerateFiles(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task StoreForWorksheetSelectionAsync_CancellationAfterStorageDeletesFinalUpload()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var localStorage = CreateStorage(rootPath);
        using var cancellationSource = new CancellationTokenSource();
        var storage = new CancelAfterStoreStorage(localStorage, cancellationSource);
        var service = CreateService(storage);
        using var content = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StoreForWorksheetSelectionAsync(
                content,
                "source.xlsx",
                cancellationSource.Token));

        Assert.True(storage.DeleteCalled);
        Assert.Empty(Directory.EnumerateFiles(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task StoreValidatedAsync_PreCancelledTokenCreatesNoStorage()
    {
        var (service, _, rootPath) = CreateService();
        using var cancellationSource = new CancellationTokenSource();
        using var content = Csv("Id\n1");
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StoreValidatedAsync(
                content,
                "source.csv",
                SourceType.Csv,
                new SourceOptions(),
                cancellationSource.Token));

        Assert.False(Directory.Exists(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task StoreValidatedAsync_InvalidSourceOptionsPropagateAfterCleanup()
    {
        var (service, _, rootPath) = CreateService();
        using var content = Csv("Id\n1");
        var options = new SourceOptions { Delimiter = (CsvDelimiter)999 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.StoreValidatedAsync(
                content,
                "source.csv",
                SourceType.Csv,
                options,
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(rootPath));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task StoreValidatedAsync_CleanupFailurePreservesValidationResultAndLeavesRecoverableOrphan()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        var localStorage = CreateStorage(rootPath);
        var storage = new ReleaseThenFailStorage(
            localStorage,
            DateTimeOffset.UtcNow.AddMinutes(-16));
        var service = CreateService(storage);
        using var content = Csv("A,B\n1,bad\"value");

        var result = await service.StoreValidatedAsync(
            content,
            "source.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.InvalidSourceFile);
        Assert.NotNull(storage.FailedUploadPath);
        Assert.True(File.Exists(storage.FailedUploadPath));

        await localStorage.DeleteExpiredAsync(
            DateTimeOffset.UtcNow.AddMinutes(-15),
            CancellationToken.None);

        Assert.False(File.Exists(storage.FailedUploadPath));
    }

    [Fact]
    public async Task StoreValidatedAsync_SystemFailurePropagatesAfterCleanupWithoutDeletingUnrelatedFile()
    {
        var rootPath = Path.Combine(_testDirectory, "uploads");
        Directory.CreateDirectory(rootPath);
        var unrelatedPath = Path.Combine(rootPath, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedPath, "keep");
        var storage = new MissingFileStorage(rootPath);
        var service = CreateService(storage);
        using var content = Csv("Id\n1");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.StoreValidatedAsync(
                content,
                "source.csv",
                SourceType.Csv,
                new SourceOptions(),
                CancellationToken.None));

        Assert.True(storage.DeleteCalled);
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelatedPath));
    }

    [Fact]
    [Trait("Category", "LargeFile")]
    public async Task StoreValidatedAsync_AcceptsTrackedCsvWithExactly100000DataRows()
    {
        var (service, storage, _) = CreateService();
        await using var content = File.OpenRead(GetPerformanceCsvPath());

        var result = await service.StoreValidatedAsync(
            content,
            "performance.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        Assert.True(result.IsValid);
        await storage.DeleteAsync(result.Upload!, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "LargeFile")]
    public async Task StoreValidatedAsync_RejectsCsvWith100001DataRows()
    {
        var sourcePath = GetPerformanceCsvPath();
        var oversizedPath = Path.Combine(_testDirectory, "performance-100001.csv");
        Directory.CreateDirectory(_testDirectory);
        File.Copy(sourcePath, oversizedPath);
        await File.AppendAllTextAsync(
            oversizedPath,
            "100001,Performance User 100001,user100001@example.com,38,2000.37,1976-06-14,Turkey\n");
        var (service, _, rootPath) = CreateService();
        await using var content = File.OpenRead(oversizedPath);

        var result = await service.StoreValidatedAsync(
            content,
            "performance.csv",
            SourceType.Csv,
            new SourceOptions(),
            CancellationToken.None);

        AssertRejected(result, UploadValidationFailureCode.RowLimitExceeded);
        Assert.Empty(Directory.EnumerateFiles(rootPath));
    }

    [Theory]
    [InlineData(100_000, true)]
    [InlineData(100_001, false)]
    [Trait("Category", "LargeFile")]
    public async Task StoreValidatedAsync_EnforcesXlsx100000DataRowBoundary(
        int dataRowCount,
        bool expectedValid)
    {
        var workbookPath = Path.Combine(_testDirectory, $"boundary-{dataRowCount}.xlsx");
        Directory.CreateDirectory(_testDirectory);
        Extraction.StreamingLargeXlsxFixture.Create(workbookPath, dataRowCount);
        var (service, storage, rootPath) = CreateService();
        await using var content = File.OpenRead(workbookPath);

        var result = await service.StoreValidatedAsync(
            content,
            "boundary.xlsx",
            SourceType.Xlsx,
            XlsxOptions(Extraction.StreamingLargeXlsxFixture.WorksheetName),
            CancellationToken.None);

        Assert.Equal(expectedValid, result.IsValid);

        if (expectedValid)
        {
            await storage.DeleteAsync(result.Upload!, CancellationToken.None);
        }
        else
        {
            AssertRejected(result, UploadValidationFailureCode.RowLimitExceeded);
        }

        Assert.Empty(Directory.EnumerateFiles(rootPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private (UploadValidationService Service, LocalUploadStorage Storage, string RootPath) CreateService(
        long maxFileSizeBytes = 100L * 1024 * 1024,
        int maxDataRowCount = 100_000)
    {
        return CreateService(
            Path.Combine(_testDirectory, "uploads"),
            maxFileSizeBytes,
            maxDataRowCount);
    }

    private static (UploadValidationService Service, LocalUploadStorage Storage, string RootPath) CreateService(
        string rootPath,
        long maxFileSizeBytes = 100L * 1024 * 1024,
        int maxDataRowCount = 100_000)
    {
        var storage = CreateStorage(rootPath);
        return (
            CreateService(storage, maxFileSizeBytes, maxDataRowCount),
            storage,
            rootPath);
    }

    private static UploadValidationService CreateService(
        IUploadStorage storage,
        long maxFileSizeBytes = 100L * 1024 * 1024,
        int maxDataRowCount = 100_000)
    {
        return new UploadValidationService(
            storage,
            new UploadValidationOptions
            {
                MaxFileSizeBytes = maxFileSizeBytes,
                MaxDataRowCount = maxDataRowCount
            },
            new CsvFileExtractor(),
            new XlsxFileExtractor());
    }

    private static LocalUploadStorage CreateStorage(string rootPath)
    {
        return new LocalUploadStorage(
            new UploadStorageOptions { RootPath = Path.GetFullPath(rootPath) });
    }

    private static MemoryStream Csv(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static SourceOptions XlsxOptions(string? worksheetName)
    {
        return new SourceOptions { WorksheetName = worksheetName };
    }

    private static void AssertRejected(
        UploadValidationResult result,
        UploadValidationFailureCode expectedCode)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Upload);
        Assert.Equal(expectedCode, Assert.IsType<UploadValidationFailure>(result.Failure).Code);
    }

    private static string GetPerformanceCsvPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "performance-100k.csv");
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

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

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CancelAfterStoreStorage(
        IUploadStorage inner,
        CancellationTokenSource cancellationSource) : IUploadStorage
    {
        public bool DeleteCalled { get; private set; }

        public async Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken)
        {
            var upload = await inner.StoreAsync(content, originalFileName, cancellationToken);
            cancellationSource.Cancel();
            return upload;
        }

        public Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken)
        {
            DeleteCalled = true;
            return inner.DeleteAsync(upload, cancellationToken);
        }

        public Task DeleteExpiredAsync(
            DateTimeOffset expiresBefore,
            CancellationToken cancellationToken) =>
            inner.DeleteExpiredAsync(expiresBefore, cancellationToken);
    }

    private sealed class ReleaseThenFailStorage(
        IUploadStorage inner,
        DateTimeOffset orphanLastWriteTime) : IUploadStorage
    {
        public string? FailedUploadPath { get; private set; }

        public Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken) =>
            inner.StoreAsync(content, originalFileName, cancellationToken);

        public async Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken)
        {
            var content = await File.ReadAllBytesAsync(upload.StoredFilePath, cancellationToken);
            await inner.DeleteAsync(upload, cancellationToken);
            await File.WriteAllBytesAsync(upload.StoredFilePath, content, CancellationToken.None);
            File.SetLastWriteTimeUtc(upload.StoredFilePath, orphanLastWriteTime.UtcDateTime);
            FailedUploadPath = upload.StoredFilePath;
            throw new IOException("Simulated physical deletion failure after ownership release.");
        }

        public Task DeleteExpiredAsync(
            DateTimeOffset expiresBefore,
            CancellationToken cancellationToken) =>
            inner.DeleteExpiredAsync(expiresBefore, cancellationToken);
    }

    private sealed class MissingFileStorage(string rootPath) : IUploadStorage
    {
        public bool DeleteCalled { get; private set; }

        public Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken)
        {
            var storedFileName = $"{Guid.NewGuid():N}.upload";
            return Task.FromResult(
                new StoredUpload(
                    originalFileName,
                    storedFileName,
                    Path.Combine(rootPath, storedFileName),
                    1));
        }

        public Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken)
        {
            DeleteCalled = true;
            return Task.CompletedTask;
        }

        public Task DeleteExpiredAsync(
            DateTimeOffset expiresBefore,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UploadValidationTestCollection
{
    public const string Name = "Upload validation";
}
