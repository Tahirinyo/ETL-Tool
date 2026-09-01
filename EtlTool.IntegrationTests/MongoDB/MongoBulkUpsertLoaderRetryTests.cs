using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.MongoDB;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoBulkUpsertLoaderRetryTests
{
    [Fact]
    public async Task PrepareAsync_UsesPipelineTargetAndUpsertKeyWithOriginalCancellationToken()
    {
        var access = new RecordingTargetAccessService();
        var loader = Loader(
            new StubWriter(),
            maximumAttempts: 1,
            delayMilliseconds: 0,
            access);
        var pipeline = new PipelineDefinition
        {
            DestinationType = DestinationType.MongoDb,
            DestinationDatabase = "target_db",
            DestinationCollection = "rows",
            UpsertKeyField = "id"
        };
        using var cancellation = new CancellationTokenSource();

        await loader.PrepareAsync(pipeline, cancellation.Token);

        Assert.Equal(DestinationType.MongoDb, loader.DestinationType);
        Assert.Equal(new MongoTarget("target_db", "rows"), access.AccessTarget);
        Assert.Equal(access.AccessTarget, access.IndexTarget);
        Assert.Equal("id", access.UpsertKeyField);
        Assert.Equal(cancellation.Token, access.AccessToken);
        Assert.Equal(cancellation.Token, access.IndexToken);
    }

    [Fact]
    public async Task Loader_CreatesOneReplacementUpsertPerRowUsingCanonicalKey()
    {
        var writer = new StubWriter();
        var loader = Loader(writer, maximumAttempts: 1, delayMilliseconds: 0);

        await loader.UpsertBatchAsync(
            [Row(2, ("id", 1L), ("name", "Ada")), Row(3, ("id", 2), ("name", "Grace"))],
            Target(),
            "id",
            CancellationToken.None);

        Assert.Equal(1, writer.Attempts);
        Assert.Equal(2, writer.LastRequests.Count);
        var first = Assert.IsType<ReplaceOneModel<BsonDocument>>(writer.LastRequests[0]);
        var filter = Assert.IsType<BsonDocumentFilterDefinition<BsonDocument>>(first.Filter).Document;
        Assert.True(first.IsUpsert);
        Assert.Equal(Collation.Simple, first.Collation);
        Assert.Equal(BsonType.Decimal128, filter["id"].BsonType);
        Assert.Equal(filter["id"], first.Replacement["id"]);
        Assert.Equal("Ada", first.Replacement["name"].AsString);
    }

    [Fact]
    public async Task Loader_StoresCanonicalUnspecifiedDatesAtUtcClockFieldsWithoutLocalShift()
    {
        var writer = new StubWriter();
        var loader = Loader(writer, maximumAttempts: 1, delayMilliseconds: 0);
        var canonicalDate = new DateTime(2026, 6, 15);

        await loader.UpsertBatchAsync(
            [Row(2, ("event_date", canonicalDate), ("raw_date", canonicalDate))],
            Target(),
            "event_date",
            CancellationToken.None);

        var request = Assert.IsType<ReplaceOneModel<BsonDocument>>(
            Assert.Single(writer.LastRequests));
        var expected = new BsonDateTime(
            DateTime.SpecifyKind(canonicalDate, DateTimeKind.Utc));

        Assert.Equal(expected, request.Replacement["event_date"]);
        Assert.Equal(expected, request.Replacement["raw_date"]);
    }

    [Fact]
    public async Task SafeNoWriteFailure_RetriesThenReturnsConfirmedResult()
    {
        var writer = new StubWriter
        {
            Execute = attempt => attempt == 1
                ? Task.FromException<BatchLoadResult>(SafeNoWriteFailure())
                : Task.FromResult(new BatchLoadResult(1, 0))
        };
        var loader = Loader(writer, maximumAttempts: 3, delayMilliseconds: 0);

        var result = await loader.UpsertBatchAsync(
            [Row(2, ("id", 1L))],
            Target(),
            "id",
            CancellationToken.None);

        Assert.Equal(2, writer.Attempts);
        Assert.Equal(1, result.InsertedRows);
    }

    [Fact]
    public async Task SafeNoWriteFailure_StopsAtConfiguredAttemptLimit()
    {
        var writer = new StubWriter
        {
            Execute = _ => Task.FromException<BatchLoadResult>(SafeNoWriteFailure())
        };
        var loader = Loader(writer, maximumAttempts: 3, delayMilliseconds: 0);

        var failure = await Assert.ThrowsAsync<BatchLoadException>(() =>
            loader.UpsertBatchAsync(
                [Row(2, ("id", "A"))],
                Target(),
                "id",
                CancellationToken.None));

        Assert.Equal(3, writer.Attempts);
        Assert.False(failure.ConfirmedResult.HasCommittedRows);
    }

    [Fact]
    public async Task AmbiguousOrPartialFailure_IsNeverBlindlyRetried()
    {
        var ambiguousWriter = new StubWriter
        {
            Execute = _ => Task.FromException<BatchLoadResult>(LabeledFailure("NoWritesPerformed"))
        };
        var ambiguousLoader = Loader(ambiguousWriter, maximumAttempts: 3, delayMilliseconds: 0);

        var ambiguous = await Assert.ThrowsAsync<BatchLoadException>(() =>
            ambiguousLoader.UpsertBatchAsync(
                [Row(2, ("id", "A"))],
                Target(),
                "id",
                CancellationToken.None));

        Assert.Equal(1, ambiguousWriter.Attempts);
        Assert.False(ambiguous.ConfirmedResult.HasCommittedRows);

        var partial = new BatchLoadException(
            "Partial result.",
            new BatchLoadResult(1, 0),
            new IOException("Simulated partial write."));
        var partialWriter = new StubWriter
        {
            Execute = _ => Task.FromException<BatchLoadResult>(partial)
        };
        var partialLoader = Loader(partialWriter, maximumAttempts: 3, delayMilliseconds: 0);

        var observed = await Assert.ThrowsAsync<BatchLoadException>(() =>
            partialLoader.UpsertBatchAsync(
                [Row(2, ("id", "A"))],
                Target(),
                "id",
                CancellationToken.None));

        Assert.Same(partial, observed);
        Assert.Equal(1, partialWriter.Attempts);
        Assert.Equal(1, observed.ConfirmedResult.InsertedRows);
    }

    [Fact]
    public async Task Cancellation_BeforeWriteOrDuringRetryDelayStopsPromptly()
    {
        var beforeWriter = new StubWriter();
        var beforeLoader = Loader(beforeWriter, maximumAttempts: 3, delayMilliseconds: 10_000);
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            beforeLoader.UpsertBatchAsync(
                [Row(2, ("id", "A"))],
                Target(),
                "id",
                alreadyCancelled.Token));
        Assert.Equal(0, beforeWriter.Attempts);

        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayWriter = new StubWriter
        {
            Execute = _ =>
            {
                firstAttempt.TrySetResult();
                return Task.FromException<BatchLoadResult>(SafeNoWriteFailure());
            }
        };
        var delayLoader = Loader(delayWriter, maximumAttempts: 3, delayMilliseconds: 10_000);
        using var cancellation = new CancellationTokenSource();
        var execution = delayLoader.UpsertBatchAsync(
            [Row(2, ("id", "A"))],
            Target(),
            "id",
            cancellation.Token);

        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(1, delayWriter.Attempts);
    }

    [Fact]
    public async Task Loader_PreflightsKeyAndIdShapeBeforeWriterInvocation()
    {
        var writer = new StubWriter();
        var loader = Loader(writer, maximumAttempts: 1, delayMilliseconds: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loader.UpsertBatchAsync(
                [Row(2, ("id", "A"), ("_id", "different"))],
                Target(),
                "id",
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loader.UpsertBatchAsync(
                [Row(2, ("id", " "))],
                Target(),
                "id",
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loader.UpsertBatchAsync(
                [Row(2, ("other", "A"))],
                Target(),
                "id",
                CancellationToken.None));

        Assert.Equal(0, writer.Attempts);
    }

    private static MongoBulkUpsertLoader Loader(
        IMongoBulkWriteExecutor writer,
        int maximumAttempts,
        int delayMilliseconds,
        IMongoTargetAccessService? targetAccessService = null)
    {
        var options = new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = $"etl_tool_loader_tests_{Guid.NewGuid():N}",
            BulkWriteMaximumAttempts = maximumAttempts,
            BulkWriteRetryDelayMilliseconds = delayMilliseconds
        };
        return new MongoBulkUpsertLoader(
            new MongoMetadataDatabase(options),
            targetAccessService ?? AllowedTargetAccessService.Instance,
            options,
            writer);
    }

    private static MongoTarget Target() => new("target_db", "rows");

    private static MongoException SafeNoWriteFailure() =>
        LabeledFailure("NoWritesPerformed", "RetryableWriteError");

    private static MongoException LabeledFailure(params string[] labels)
    {
        var exception = new MongoException("Simulated MongoDB failure.");
        foreach (var label in labels)
        {
            exception.AddErrorLabel(label);
        }

        return exception;
    }

    private static DataRow Row(
        long sourceRowNumber,
        params (string Field, object? Value)[] values)
    {
        var row = new DataRow { SourceRowNumber = sourceRowNumber };
        foreach (var (field, value) in values)
        {
            row.Values.Add(field, value);
        }

        return row;
    }

    private sealed class StubWriter : IMongoBulkWriteExecutor
    {
        public int Attempts { get; private set; }

        public IReadOnlyList<WriteModel<BsonDocument>> LastRequests { get; private set; } = [];

        public Func<int, Task<BatchLoadResult>> Execute { get; init; } =
            _ => Task.FromResult(BatchLoadResult.Empty);

        public Task<BatchLoadResult> WriteAsync(
            IMongoCollection<BsonDocument> collection,
            IReadOnlyList<WriteModel<BsonDocument>> requests,
            CancellationToken cancellationToken)
        {
            Attempts++;
            LastRequests = requests;
            return Execute(Attempts);
        }
    }

    private sealed class AllowedTargetAccessService : IMongoTargetAccessService
    {
        public static AllowedTargetAccessService Instance { get; } = new();

        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(
            MongoTarget target,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureUpsertIndexAsync(
            MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingTargetAccessService : IMongoTargetAccessService
    {
        public MongoTarget? AccessTarget { get; private set; }

        public MongoTarget? IndexTarget { get; private set; }

        public string? UpsertKeyField { get; private set; }

        public CancellationToken AccessToken { get; private set; }

        public CancellationToken IndexToken { get; private set; }

        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(
            MongoTarget target,
            CancellationToken cancellationToken)
        {
            AccessTarget = target;
            AccessToken = cancellationToken;
            return Task.CompletedTask;
        }

        public Task EnsureUpsertIndexAsync(
            MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken)
        {
            IndexTarget = target;
            UpsertKeyField = upsertKeyField;
            IndexToken = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
