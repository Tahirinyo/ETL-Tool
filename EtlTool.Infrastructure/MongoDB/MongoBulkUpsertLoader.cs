using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.MongoDB;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoBulkUpsertLoader : IDataLoader
{
    private const string NoWritesPerformedLabel = "NoWritesPerformed";
    private const string RetryableWriteErrorLabel = "RetryableWriteError";

    private readonly MongoMetadataDatabase _metadataDatabase;
    private readonly IMongoTargetAccessService _targetAccessService;
    private readonly IMongoBulkWriteExecutor _writeExecutor;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _retryDelay;

    public MongoBulkUpsertLoader(
        MongoMetadataDatabase metadataDatabase,
        IMongoTargetAccessService targetAccessService,
        MongoDbOptions options)
        : this(metadataDatabase, targetAccessService, options, new MongoBulkWriteExecutor())
    {
    }

    internal MongoBulkUpsertLoader(
        MongoMetadataDatabase metadataDatabase,
        IMongoTargetAccessService targetAccessService,
        MongoDbOptions options,
        IMongoBulkWriteExecutor writeExecutor)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        ArgumentNullException.ThrowIfNull(targetAccessService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writeExecutor);
        options.Validate();

        _metadataDatabase = metadataDatabase;
        _targetAccessService = targetAccessService;
        _writeExecutor = writeExecutor;
        _maximumAttempts = options.BulkWriteMaximumAttempts;
        _retryDelay = TimeSpan.FromMilliseconds(options.BulkWriteRetryDelayMilliseconds);
    }

    public DestinationType DestinationType => DestinationType.MongoDb;

    public async Task PrepareAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        var target = CreateTarget(pipeline);

        await _targetAccessService
            .EnsureAccessibleAsync(target, cancellationToken)
            .ConfigureAwait(false);
        await _targetAccessService
            .EnsureUpsertIndexAsync(target, pipeline.UpsertKeyField, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BatchLoadResult> UpsertBatchAsync(
        IReadOnlyList<DataRow> rows,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var target = CreateTarget(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeline.UpsertKeyField);

        if (rows.Count == 0)
        {
            throw new ArgumentException("A MongoDB load batch cannot be empty.", nameof(rows));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requests = CreateRequests(rows, pipeline.UpsertKeyField);
        var collection = _metadataDatabase
            .GetDatabase(target.DatabaseName)
            .GetCollection<BsonDocument>(target.CollectionName);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await _writeExecutor
                    .WriteAsync(collection, requests, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BatchLoadException)
            {
                throw;
            }
            catch (MongoException exception)
                when (IsSafeApplicationRetry(exception) && attempt < _maximumAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (MongoException exception)
            {
                throw new BatchLoadException(
                    "MongoDB batch loading failed without a confirmed write result.",
                    BatchLoadResult.Empty,
                    exception);
            }
            catch (TimeoutException exception)
            {
                throw new BatchLoadException(
                    "MongoDB batch loading failed without a confirmed write result.",
                    BatchLoadResult.Empty,
                    exception);
            }
        }
    }

    internal Task<BatchLoadResult> UpsertBatchAsync(
        IReadOnlyList<DataRow> rows,
        MongoTarget target,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        return UpsertBatchAsync(
            rows,
            new PipelineDefinition
            {
                DestinationType = DestinationType.MongoDb,
                DestinationDatabase = target.DatabaseName,
                DestinationCollection = target.CollectionName,
                UpsertKeyField = upsertKeyField
            },
            cancellationToken);
    }

    private static MongoTarget CreateTarget(PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        if (pipeline.DestinationType != DestinationType.MongoDb)
        {
            throw new InvalidOperationException(
                $"The MongoDB data loader cannot handle destination type '{pipeline.DestinationType}'.");
        }

        return new MongoTarget(
            pipeline.DestinationDatabase,
            pipeline.DestinationCollection);
    }

    private static IReadOnlyList<WriteModel<BsonDocument>> CreateRequests(
        IReadOnlyList<DataRow> rows,
        string upsertKeyField)
    {
        if (upsertKeyField.Contains('.', StringComparison.Ordinal)
            || upsertKeyField.StartsWith('$'))
        {
            throw new InvalidOperationException(
                "The MongoDB upsert-key field cannot contain '.' or start with '$'.");
        }

        var requests = new WriteModel<BsonDocument>[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]
                ?? throw new ArgumentException("The MongoDB load batch contains an invalid row.", nameof(rows));

            if (!StringComparer.Ordinal.Equals(upsertKeyField, "_id")
                && row.Values.ContainsKey("_id"))
            {
                throw new InvalidOperationException(
                    "A mapped '_id' field is supported only when '_id' is the configured upsert key.");
            }

            var identity = UpsertKeyIdentity.Create(row, upsertKeyField);
            var document = ToDocument(row);
            var keyValue = ToBsonValue(identity.EffectiveValue);
            document[upsertKeyField] = keyValue;

            requests[index] = new ReplaceOneModel<BsonDocument>(
                new BsonDocument(upsertKeyField, keyValue),
                document)
            {
                IsUpsert = true,
                Collation = Collation.Simple
            };
        }

        return requests;
    }

    private static BsonDocument ToDocument(DataRow row)
    {
        var document = new BsonDocument();
        foreach (var entry in row.Values)
        {
            document.Add(entry.Key, ToBsonValue(entry.Value));
        }

        return document;
    }

    private static BsonValue ToBsonValue(object? value) => value switch
    {
        null => BsonNull.Value,
        // Shared date parsing intentionally produces timezone-free DateTime values.
        // Match upsert-key identity by treating those clock fields as UTC without shifting them.
        DateTime { Kind: DateTimeKind.Unspecified } dateTime =>
            new BsonDateTime(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => BsonValue.Create(value)
    };

    private static bool IsSafeApplicationRetry(MongoException exception) =>
        exception.HasErrorLabel(NoWritesPerformedLabel)
        && exception.HasErrorLabel(RetryableWriteErrorLabel);
}
