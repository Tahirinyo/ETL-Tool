using System.Diagnostics;
using System.Runtime.InteropServices;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using Xunit.Abstractions;

namespace EtlTool.IntegrationTests.Execution;

[Collection("Task 13.2 batch execution acceptance")]
[Trait("Category", "Performance")]
public sealed class BatchOrchestratorPerformanceAcceptanceTests(ITestOutputHelper output)
{
    private const int DataRowCount = 100_000;
    private const int BatchSize = 1_000;
    private readonly ITestOutputHelper _output = output;

    [BatchExecutionAcceptanceFact]
    public async Task ExecuteAsync_Processes100kCsvIncrementallyInConfiguredBatches()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "performance-100k.csv");
        Assert.True(File.Exists(path), $"The performance CSV was not copied to '{path}'.");
        Assert.Equal(DataRowCount + 1L, File.ReadLines(path).LongCount());

        var inputBytes = new FileInfo(path).Length;
        var pipeline = CreatePipeline();
        var orchestrator = CreateOrchestrator();
        var snapshots = new List<MemorySnapshot>();
        var batchesProcessed = 0;
        var rowsLoaded = 0L;
        long bytesReadAtFirstBatch = 0;

        ForceFullCollection();
        snapshots.Add(CaptureSnapshot("Baseline", 0, TimeSpan.Zero));
        var stopwatch = Stopwatch.StartNew();

        await using (var source = new TrackingReadStream(File.OpenRead(path)))
        {
            var result = await orchestrator.ExecuteAsync(
                source,
                pipeline,
                (batch, _) =>
                {
                    batchesProcessed++;
                    rowsLoaded += batch.Count;
                    Assert.Equal(BatchSize, batch.Count);
                    Assert.All(batch, row => Assert.Equal(7, row.Values.Count));

                    if (batchesProcessed == 1)
                    {
                        bytesReadAtFirstBatch = source.BytesRead;
                    }

                    snapshots.Add(CaptureSnapshot(
                        $"Batch{batchesProcessed}",
                        rowsLoaded,
                        stopwatch.Elapsed));
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask,
                CancellationToken.None);

            stopwatch.Stop();

            Assert.Equal(DataRowCount, result.ProcessedRows);
            Assert.Equal(DataRowCount, result.ValidRows);
            Assert.Equal(0, result.InvalidRows);
            Assert.Equal(0, result.FilteredRows);
            Assert.Equal(0, result.DeduplicatedRows);
            Assert.Equal(DataRowCount / BatchSize, batchesProcessed);
            Assert.Equal(DataRowCount, rowsLoaded);
            Assert.True(bytesReadAtFirstBatch > 0);
            Assert.True(
                bytesReadAtFirstBatch < inputBytes,
                "The first configured batch was not delivered until the entire source was physically read.");
            Assert.True(source.BytesRead >= bytesReadAtFirstBatch);
        }

        ForceFullCollection();
        snapshots.Add(CaptureSnapshot("PostRun", DataRowCount, stopwatch.Elapsed));
        WriteMeasurements(inputBytes, bytesReadAtFirstBatch, stopwatch.Elapsed, snapshots);
    }

    private static BatchOrchestrator CreateOrchestrator()
    {
        var mapping = new FieldMappingService();
        var transformations = new TransformationEngine(new TransformationHandlerRegistry(
        [
            new TrimTransformationHandler(),
            new ConvertToIntegerTransformationHandler(),
            new ConvertToDecimalTransformationHandler(),
            new ConvertToDateTransformationHandler()
        ]));
        var validations = new ValidationEngine(new ValidationHandlerRegistry(
        [
            new RequiredValidationHandler(),
            new EmailValidationHandler(),
            new NumericRangeValidationHandler(),
            new DateRangeValidationHandler()
        ]));
        var targetAccess = new AllowedTargetAccessService();

        return new BatchOrchestrator(
            new FileExtractorResolver([new CsvFileExtractor()]),
            new PipelineReadinessService(new NullRepository(), mapping, targetAccess),
            new PipelineRowProcessor(mapping, transformations, validations),
            targetAccess,
            new BatchExecutionOptions { BatchSize = BatchSize });
    }

    private static PipelineDefinition CreatePipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "100K performance acceptance",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            CultureName = "en-US",
            DateFormat = "yyyy-MM-dd",
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema =
        [
            Field("CustomerId", SourceFieldType.Integer),
            Field("FullName", SourceFieldType.String),
            Field("Email", SourceFieldType.String),
            Field("Age", SourceFieldType.Integer),
            Field("Balance", SourceFieldType.Decimal),
            Field("BirthDate", SourceFieldType.Date),
            Field("Country", SourceFieldType.String)
        ],
        FieldMappings =
        [
            Mapping("CustomerId", "customerId"),
            Mapping("FullName", "fullName"),
            Mapping("Email", "email"),
            Mapping("Age", "age"),
            Mapping("Balance", "balance"),
            Mapping("BirthDate", "birthDate"),
            Mapping("Country", "country")
        ],
        TransformationRules =
        [
            Rule(1, TransformationType.Trim, "fullName"),
            Rule(2, TransformationType.ConvertToInteger, "age"),
            Rule(3, TransformationType.ConvertToDecimal, "balance"),
            Rule(4, TransformationType.ConvertToDate, "birthDate")
        ],
        ValidationRules =
        [
            Validation(ValidationType.Required, "customerId"),
            Validation(ValidationType.EmailFormat, "email"),
            Validation(ValidationType.NumericRange, "age", ("Minimum", "0"), ("Maximum", "150")),
            Validation(ValidationType.DateRange, "birthDate", ("Minimum", "1900-01-01"), ("Maximum", "2100-01-01"))
        ],
        DestinationDatabase = "acceptance",
        DestinationCollection = "customers",
        UpsertKeyField = "customerId"
    };

    private void WriteMeasurements(
        long inputBytes,
        long bytesReadAtFirstBatch,
        TimeSpan elapsed,
        IReadOnlyList<MemorySnapshot> snapshots)
    {
        var peak = snapshots.MaxBy(snapshot => snapshot.PrivateMemoryBytes)!;
        var baseline = snapshots[0];
        var postRun = snapshots[^1];

        _output.WriteLine($"Input: {DataRowCount:N0} data rows, {inputBytes:N0} bytes.");
        _output.WriteLine($"Configuration: batch size {BatchSize:N0}; CSV; en-US; yyyy-MM-dd.");
        _output.WriteLine($"Environment: {RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}.");
        _output.WriteLine($"Incremental read: first batch after {bytesReadAtFirstBatch:N0}/{inputBytes:N0} bytes.");
        _output.WriteLine($"Elapsed: {elapsed.TotalMilliseconds:F0} ms.");
        _output.WriteLine("Snapshot           Rows  ManagedMiB  WorkingSetMiB  PrivateMiB");
        foreach (var snapshot in new[] { baseline, peak, postRun }.Distinct())
        {
            _output.WriteLine(
                $"{snapshot.Name,-16} {snapshot.RowsProcessed,6:N0} " +
                $"{ToMiB(snapshot.ManagedBytes),11:F2} {ToMiB(snapshot.WorkingSetBytes),14:F2} {ToMiB(snapshot.PrivateMemoryBytes),11:F2}");
        }
    }

    private static MemorySnapshot CaptureSnapshot(
        string name,
        long rowsProcessed,
        TimeSpan elapsed)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemorySnapshot(
            name,
            rowsProcessed,
            elapsed,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            process.PrivateMemorySize64);
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static SourceFieldDefinition Field(string name, SourceFieldType type) => new()
    {
        Name = name,
        DataType = type
    };

    private static FieldMapping Mapping(string source, string target) => new()
    {
        SourceField = source,
        TargetField = target,
        IsIncluded = true
    };

    private static TransformationRule Rule(int order, TransformationType type, string sourceField) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type,
        SourceField = sourceField
    };

    private static ValidationRule Validation(
        ValidationType type,
        string field,
        params (string Key, string Value)[] configuration) => new()
    {
        Type = type,
        Field = field,
        Configuration = configuration.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal)
    };

    private static double ToMiB(long bytes) => bytes / 1024d / 1024d;

    private sealed class AllowedTargetAccessService : IMongoTargetAccessService
    {
        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullRepository : IPipelineDefinitionRepository
    {
        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Track(inner.Read(buffer));

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
            CancellationToken cancellationToken) =>
            Track(await inner.ReadAsync(buffer, offset, count, cancellationToken));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Track(await inner.ReadAsync(buffer, cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

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

    private sealed record MemorySnapshot(
        string Name,
        long RowsProcessed,
        TimeSpan Elapsed,
        long ManagedBytes,
        long WorkingSetBytes,
        long PrivateMemoryBytes);
}

[CollectionDefinition("Task 13.2 batch execution acceptance", DisableParallelization = true)]
public sealed class BatchExecutionAcceptanceCollectionDefinition;

[AttributeUsage(AttributeTargets.Method)]
public sealed class BatchExecutionAcceptanceFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "ETL_RUN_100K_EXECUTION_ACCEPTANCE";

    public BatchExecutionAcceptanceFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {EnvironmentVariable}=1 to run the 100,000-row batch-execution acceptance test.";
        }
    }
}
