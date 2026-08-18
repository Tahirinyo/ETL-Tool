using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EtlTool.Application.Extraction;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using ExcelDataReader;
using Xunit.Abstractions;

namespace EtlTool.IntegrationTests.Extraction;

[Collection("Task 3A.3 extraction experiment")]
[Trait("Category", "Performance")]
public sealed class ExtractionMemoryExperimentTests(ITestOutputHelper output)
{
    private const int DataRowCount = 100_000;
    private const int RepetitionCount = 3;
    private const int EarlyConsumptionRowCount = 10;
    private const int CancellationRowCount = 10_000;

    private readonly ITestOutputHelper _output = output;

    [ExtractionExperimentFact]
    public async Task ReadAsync_Processes100kCsvAndXlsxWithoutRetainingEmittedRows()
    {
        var csvPath = Path.Combine(AppContext.BaseDirectory, "TestData", "performance-100k.csv");
        Assert.True(File.Exists(csvPath), $"The performance CSV was not copied to '{csvPath}'.");

        var csvHeader = File.ReadLines(csvPath).First();
        var csvPhysicalLineCount = File.ReadLines(csvPath).LongCount();
        Assert.Equal(
            "CustomerId,FullName,Email,Age,Balance,BirthDate,Country",
            csvHeader);
        Assert.Equal(DataRowCount + 1L, csvPhysicalLineCount);

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"EtlTool-Task3A3-{Guid.NewGuid():N}");
        var xlsxPath = Path.Combine(temporaryDirectory, "performance-100k.xlsx");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            StreamingLargeXlsxFixture.Create(xlsxPath, DataRowCount);
            Assert.True(File.Exists(xlsxPath));

            await WarmUpAsync();
            ForceFullCollection();

            var csvRuns = new List<ExperimentRun>(RepetitionCount);
            var xlsxRuns = new List<ExperimentRun>(RepetitionCount);

            for (var repetition = 1; repetition <= RepetitionCount; repetition++)
            {
                csvRuns.Add(
                    await MeasureFullEnumerationAsync(
                        "CSV",
                        repetition,
                        csvPath,
                        new CsvFileExtractor(),
                        new SourceOptions()));
            }

            for (var repetition = 1; repetition <= RepetitionCount; repetition++)
            {
                xlsxRuns.Add(
                    await MeasureFullEnumerationAsync(
                        "XLSX",
                        repetition,
                        xlsxPath,
                        new XlsxFileExtractor(),
                        new SourceOptions { WorksheetName = StreamingLargeXlsxFixture.WorksheetName }));
            }

            var earlyConsumption = new[]
            {
                await MeasureEarlyConsumptionAsync(
                    "CSV",
                    csvPath,
                    new CsvFileExtractor(),
                    new SourceOptions(),
                    requirePartialPhysicalRead: true),
                await MeasureEarlyConsumptionAsync(
                    "XLSX",
                    xlsxPath,
                    new XlsxFileExtractor(),
                    new SourceOptions { WorksheetName = StreamingLargeXlsxFixture.WorksheetName },
                    requirePartialPhysicalRead: false)
            };
            var cancellations = new[]
            {
                await MeasureCancellationAsync(
                    "CSV",
                    csvPath,
                    new CsvFileExtractor(),
                    new SourceOptions()),
                await MeasureCancellationAsync(
                    "XLSX",
                    xlsxPath,
                    new XlsxFileExtractor(),
                    new SourceOptions { WorksheetName = StreamingLargeXlsxFixture.WorksheetName })
            };

            var report = new ExperimentReport(
                DateTimeOffset.UtcNow,
                new ExperimentEnvironment(
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    typeof(ExtractionMemoryExperimentTests).Assembly
                        .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Unknown",
                    typeof(CsvHelper.CsvReader).Assembly.GetName().Version?.ToString() ?? "Unknown",
                    typeof(IExcelDataReader).Assembly.GetName().Version?.ToString() ?? "Unknown"),
                new[]
                {
                    await CreateFixtureIdentityAsync("CSV", csvPath, csvPhysicalLineCount - 1),
                    await CreateFixtureIdentityAsync("XLSX", xlsxPath, DataRowCount)
                },
                csvRuns.Concat(xlsxRuns).ToArray(),
                earlyConsumption,
                cancellations);

            var reportPath = await WriteReportAsync(report);
            WriteSummary(report, reportPath);
        }
        finally
        {
            if (File.Exists(xlsxPath))
            {
                File.Delete(xlsxPath);
            }

            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory);
            }
        }
    }

    private static async Task WarmUpAsync()
    {
        await using (var csvStream = new MemoryStream(Encoding.UTF8.GetBytes("Id,Name\n1,Ada\n2,Grace")))
        {
            await ConsumePrefixAsync(
                new CsvFileExtractor().ReadAsync(csvStream, new SourceOptions(), CancellationToken.None),
                rowCount: 2);
        }

        var xlsxPath = Path.Combine(AppContext.BaseDirectory, "TestData", "clean.xlsx");
        await using var xlsxStream = File.OpenRead(xlsxPath);
        await ConsumePrefixAsync(
            new XlsxFileExtractor().ReadAsync(
                xlsxStream,
                new SourceOptions { WorksheetName = "Customers" },
                CancellationToken.None),
            rowCount: 1);
    }

    private static async Task<ExperimentRun> MeasureFullEnumerationAsync(
        string format,
        int repetition,
        string path,
        IFileExtractor extractor,
        SourceOptions options)
    {
        ForceFullCollection();
        var allocationOrigin = GC.GetTotalAllocatedBytes(precise: true);
        var inputLength = new FileInfo(path).Length;
        var checkpoints = new List<MemoryCheckpoint>(capacity: 6)
        {
            CaptureCheckpoint(
                "Baseline",
                rowsConsumed: 0,
                bytesRead: 0,
                elapsed: TimeSpan.Zero,
                allocationOrigin)
        };
        var stopwatch = Stopwatch.StartNew();
        var rowCount = 0L;
        var lastSourceRowNumber = 0L;
        var finalBytesRead = 0L;
        WeakReference<DataRow>? tenThousandRow = null;
        WeakReference<DataRow>? fiftyThousandRow = null;
        var tenThousandRowCollected = false;
        var fiftyThousandRowCollected = false;

        await using (var stream = new TrackingReadStream(File.OpenRead(path)))
        {
            await using (var enumerator = extractor
                .ReadAsync(stream, options, CancellationToken.None)
                .GetAsyncEnumerator())
            {
                while (await enumerator.MoveNextAsync())
                {
                    rowCount++;
                    lastSourceRowNumber = enumerator.Current.SourceRowNumber;

                    if (rowCount == 10_000)
                    {
                        tenThousandRow = Observe(enumerator.Current);
                    }
                    else if (rowCount == 50_000)
                    {
                        fiftyThousandRow = Observe(enumerator.Current);
                    }

                    if (rowCount is 1 or 10_000 or 50_000 or 100_000)
                    {
                        checkpoints.Add(
                            CaptureCheckpoint(
                                rowCount == 1 ? "FirstRow" : $"Row{rowCount}",
                                rowCount,
                                stream.BytesRead,
                                stopwatch.Elapsed,
                                allocationOrigin));
                    }

                    if (rowCount == 50_000)
                    {
                        Assert.NotNull(tenThousandRow);
                        tenThousandRowCollected = !tenThousandRow.TryGetTarget(out _);
                        Assert.True(
                            tenThousandRowCollected,
                            $"{format} retained the DataRow emitted at row 10,000 after advancing to row 50,000.");
                    }
                    else if (rowCount == 100_000)
                    {
                        Assert.NotNull(fiftyThousandRow);
                        fiftyThousandRowCollected = !fiftyThousandRow.TryGetTarget(out _);
                        Assert.True(
                            fiftyThousandRowCollected,
                            $"{format} retained the DataRow emitted at row 50,000 after advancing to row 100,000.");
                    }
                }
            }

            finalBytesRead = stream.BytesRead;
            Assert.True(stream.CanRead);
        }

        stopwatch.Stop();
        checkpoints.Add(
            CaptureCheckpoint(
                "Disposed",
                rowCount,
                finalBytesRead,
                stopwatch.Elapsed,
                allocationOrigin));

        Assert.Equal(DataRowCount, rowCount);
        Assert.Equal(DataRowCount + 1L, lastSourceRowNumber);
        Assert.True(tenThousandRowCollected);
        Assert.True(fiftyThousandRowCollected);

        return new ExperimentRun(
            format,
            repetition,
            inputLength,
            rowCount,
            lastSourceRowNumber,
            tenThousandRowCollected,
            fiftyThousandRowCollected,
            checkpoints);
    }

    private static async Task<EarlyConsumptionResult> MeasureEarlyConsumptionAsync(
        string format,
        string path,
        IFileExtractor extractor,
        SourceOptions options,
        bool requirePartialPhysicalRead)
    {
        var inputLength = new FileInfo(path).Length;
        var rowsConsumed = 0;
        long bytesRead;

        await using var stream = new TrackingReadStream(File.OpenRead(path));
        var rows = extractor.ReadAsync(stream, options, CancellationToken.None);
        Assert.Equal(0, stream.BytesRead);

        await using (var enumerator = rows.GetAsyncEnumerator())
        {
            while (rowsConsumed < EarlyConsumptionRowCount && await enumerator.MoveNextAsync())
            {
                rowsConsumed++;
            }
        }

        bytesRead = stream.BytesRead;
        Assert.Equal(EarlyConsumptionRowCount, rowsConsumed);
        Assert.True(bytesRead > 0);
        if (requirePartialPhysicalRead)
        {
            Assert.True(bytesRead < inputLength);
        }

        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.NotEqual(-1, stream.ReadByte());

        return new EarlyConsumptionResult(format, rowsConsumed, bytesRead, inputLength);
    }

    private static async Task<CancellationResult> MeasureCancellationAsync(
        string format,
        string path,
        IFileExtractor extractor,
        SourceOptions options)
    {
        using var cancellationSource = new CancellationTokenSource();
        await using var stream = new TrackingReadStream(File.OpenRead(path));
        var rowsConsumed = 0;

        await using (var enumerator = extractor
            .ReadAsync(stream, options, cancellationSource.Token)
            .GetAsyncEnumerator())
        {
            while (rowsConsumed < CancellationRowCount && await enumerator.MoveNextAsync())
            {
                rowsConsumed++;
            }

            cancellationSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await enumerator.MoveNextAsync();
            });
        }

        Assert.Equal(CancellationRowCount, rowsConsumed);
        Assert.True(stream.CanRead);
        ForceFullCollection();

        return new CancellationResult(
            format,
            rowsConsumed,
            stream.BytesRead,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    private static async Task ConsumePrefixAsync(IAsyncEnumerable<DataRow> rows, int rowCount)
    {
        var consumed = 0;
        await using var enumerator = rows.GetAsyncEnumerator();
        while (consumed < rowCount && await enumerator.MoveNextAsync())
        {
            consumed++;
        }

        Assert.Equal(rowCount, consumed);
    }

    private static MemoryCheckpoint CaptureCheckpoint(
        string name,
        long rowsConsumed,
        long bytesRead,
        TimeSpan elapsed,
        long allocationOrigin)
    {
        ForceFullCollection();
        var memoryInfo = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new MemoryCheckpoint(
            name,
            rowsConsumed,
            Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocationOrigin),
            GC.GetTotalMemory(forceFullCollection: false),
            memoryInfo.HeapSizeBytes,
            memoryInfo.TotalCommittedBytes,
            memoryInfo.FragmentedBytes,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            bytesRead,
            elapsed.TotalMilliseconds);
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static WeakReference<DataRow> Observe(DataRow row)
    {
        return new WeakReference<DataRow>(row);
    }

    private static async Task<FixtureIdentity> CreateFixtureIdentityAsync(
        string format,
        string path,
        long dataRows)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return new FixtureIdentity(
            format,
            new FileInfo(path).Length,
            Convert.ToHexString(hash),
            dataRows,
            Columns: 7);
    }

    private static async Task<string> WriteReportAsync(ExperimentReport report)
    {
        var repositoryRoot = FindRepositoryRoot();
        var reportDirectory = Path.Combine(repositoryRoot, "TestResults", "Task3A3");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, "extraction-memory.json");
        await using var reportStream = File.Create(reportPath);
        await JsonSerializer.SerializeAsync(
            reportStream,
            report,
            new JsonSerializerOptions { WriteIndented = true });
        return reportPath;
    }

    private void WriteSummary(ExperimentReport report, string reportPath)
    {
        _output.WriteLine(
            "Format Run FirstLiveMiB Row10kLiveMiB Row50kLiveMiB Row100kLiveMiB DisposedLiveMiB AllocatedMiB ElapsedMs");

        foreach (var run in report.Runs)
        {
            var first = run.Checkpoints.Single(checkpoint => checkpoint.Name == "FirstRow");
            var row10k = run.Checkpoints.Single(checkpoint => checkpoint.Name == "Row10000");
            var row50k = run.Checkpoints.Single(checkpoint => checkpoint.Name == "Row50000");
            var row100k = run.Checkpoints.Single(checkpoint => checkpoint.Name == "Row100000");
            var disposed = run.Checkpoints.Single(checkpoint => checkpoint.Name == "Disposed");
            _output.WriteLine(
                $"{run.Format,-5} {run.Repetition,3} " +
                $"{ToMiB(first.LiveManagedBytes),12:F2} " +
                $"{ToMiB(row10k.LiveManagedBytes),13:F2} " +
                $"{ToMiB(row50k.LiveManagedBytes),13:F2} " +
                $"{ToMiB(row100k.LiveManagedBytes),14:F2} " +
                $"{ToMiB(disposed.LiveManagedBytes),15:F2} " +
                $"{ToMiB(row100k.TotalAllocatedBytes),12:F2} " +
                $"{row100k.ElapsedMilliseconds,9:F0}");
        }

        foreach (var formatGroup in report.Runs.GroupBy(run => run.Format))
        {
            var row100kLive = formatGroup
                .Select(run => run.Checkpoints.Single(checkpoint => checkpoint.Name == "Row100000").LiveManagedBytes)
                .OrderBy(value => value)
                .ToArray();
            _output.WriteLine(
                $"{formatGroup.Key} row-100k live managed MiB min/median/max: " +
                $"{ToMiB(row100kLive[0]):F2}/{ToMiB(row100kLive[1]):F2}/{ToMiB(row100kLive[2]):F2}");
        }

        foreach (var early in report.EarlyConsumption)
        {
            _output.WriteLine(
                $"{early.Format} early stop: {early.RowsConsumed} rows, " +
                $"{early.BytesRead:N0}/{early.InputBytes:N0} bytes read.");
        }

        foreach (var cancellation in report.Cancellations)
        {
            _output.WriteLine(
                $"{cancellation.Format} cancellation: {cancellation.RowsConsumed:N0} rows, " +
                $"{cancellation.BytesRead:N0} bytes read, " +
                $"{ToMiB(cancellation.PostCancellationLiveManagedBytes):F2} MiB live after disposal.");
        }

        _output.WriteLine($"Detailed JSON report: {reportPath}");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startingPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EtlTool.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing EtlTool.sln.");
    }

    private static double ToMiB(long bytes)
    {
        return bytes / 1024d / 1024d;
    }

    private sealed class TrackingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Track(inner.Read(buffer, offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            return Track(inner.Read(buffer));
        }

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value != -1)
            {
                BytesRead++;
            }

            return value;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return Track(await inner.ReadAsync(buffer, offset, count, cancellationToken));
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return Track(await inner.ReadAsync(buffer, cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
        }

        public override ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return inner.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Track(int count)
        {
            BytesRead += count;
            return count;
        }
    }

    private sealed record ExperimentReport(
        DateTimeOffset ObservedAtUtc,
        ExperimentEnvironment Environment,
        IReadOnlyList<FixtureIdentity> Fixtures,
        IReadOnlyList<ExperimentRun> Runs,
        IReadOnlyList<EarlyConsumptionResult> EarlyConsumption,
        IReadOnlyList<CancellationResult> Cancellations);

    private sealed record ExperimentEnvironment(
        string OperatingSystem,
        string Framework,
        string ProcessArchitecture,
        string BuildConfiguration,
        string CsvHelperVersion,
        string ExcelDataReaderVersion);

    private sealed record FixtureIdentity(
        string Format,
        long InputBytes,
        string Sha256,
        long DataRows,
        int Columns);

    private sealed record ExperimentRun(
        string Format,
        int Repetition,
        long InputBytes,
        long RowsConsumed,
        long LastSourceRowNumber,
        bool TenThousandRowCollected,
        bool FiftyThousandRowCollected,
        IReadOnlyList<MemoryCheckpoint> Checkpoints);

    private sealed record MemoryCheckpoint(
        string Name,
        long RowsConsumed,
        long TotalAllocatedBytes,
        long LiveManagedBytes,
        long HeapSizeBytes,
        long TotalCommittedBytes,
        long FragmentedBytes,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long BytesRead,
        double ElapsedMilliseconds);

    private sealed record EarlyConsumptionResult(
        string Format,
        int RowsConsumed,
        long BytesRead,
        long InputBytes);

    private sealed record CancellationResult(
        string Format,
        int RowsConsumed,
        long BytesRead,
        long PostCancellationLiveManagedBytes);
}

[CollectionDefinition("Task 3A.3 extraction experiment", DisableParallelization = true)]
public sealed class ExtractionExperimentCollectionDefinition;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ExtractionExperimentFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "ETL_RUN_100K_EXTRACTION_EXPERIMENT";

    public ExtractionExperimentFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {EnvironmentVariable}=1 to run the 100,000-row extraction experiment.";
        }
    }
}
