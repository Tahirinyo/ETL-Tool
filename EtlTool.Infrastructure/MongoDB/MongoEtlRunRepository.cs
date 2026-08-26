using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.MongoDB;

public sealed class MongoEtlRunRepository : IEtlRunRepository
{
    private readonly IMongoCollection<EtlRun> _collection;

    public MongoEtlRunRepository(MongoMetadataDatabase metadataDatabase)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        _collection = metadataDatabase.EtlRuns;
    }

    public async Task AddAsync(
        EtlRun run,
        CancellationToken cancellationToken)
    {
        ValidateRun(run);

        try
        {
            await _collection.InsertOneAsync(
                run,
                options: null,
                cancellationToken);
        }
        catch (MongoWriteException exception) when (IsDuplicateIdWrite(exception))
        {
            throw new DuplicateEtlRunException(run.Id, exception);
        }
    }

    public async Task<EtlRun?> GetByIdAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        ValidateId(runId);

        return await _collection
            .Find(run => run.Id == runId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EtlRun>> ListByPipelineIdAsync(
        Guid pipelineId,
        CancellationToken cancellationToken)
    {
        ValidateId(pipelineId);

        return await _collection
            .Find(run => run.PipelineId == pipelineId)
            .SortByDescending(run => run.StartedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryStartAsync(
        Guid runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        ValidateId(runId);

        var result = await _collection.UpdateOneAsync(
            Builders<EtlRun>.Filter.And(
                Builders<EtlRun>.Filter.Eq(run => run.Id, runId),
                Builders<EtlRun>.Filter.Eq(run => run.Status, EtlRunStatus.Queued)),
            Builders<EtlRun>.Update
                .Set(run => run.Status, EtlRunStatus.Running)
                .Set(run => run.StartedAt, startedAt),
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
    }

    public async Task<bool> TryUpdateProgressAsync(
        Guid runId,
        BatchExecutionProgress progress,
        CancellationToken cancellationToken)
    {
        ValidateId(runId);
        ArgumentNullException.ThrowIfNull(progress);

        var filter = Builders<EtlRun>.Filter.And(
            Builders<EtlRun>.Filter.Eq(run => run.Id, runId),
            Builders<EtlRun>.Filter.Eq(run => run.Status, EtlRunStatus.Running),
            Builders<EtlRun>.Filter.Lte(run => run.ProcessedRows, progress.ProcessedRows),
            Builders<EtlRun>.Filter.Lte(run => run.ValidRows, progress.ValidRows),
            Builders<EtlRun>.Filter.Lte(run => run.InvalidRows, progress.InvalidRows),
            Builders<EtlRun>.Filter.Lte(run => run.FilteredRows, progress.FilteredRows),
            Builders<EtlRun>.Filter.Lte(run => run.DeduplicatedRows, progress.DeduplicatedRows),
            Builders<EtlRun>.Filter.Lte(run => run.InsertedRows, progress.InsertedRows),
            Builders<EtlRun>.Filter.Lte(run => run.UpdatedRows, progress.UpdatedRows));

        var update = Builders<EtlRun>.Update
            .Set(run => run.ProcessedRows, progress.ProcessedRows)
            .Set(run => run.ValidRows, progress.ValidRows)
            .Set(run => run.InvalidRows, progress.InvalidRows)
            .Set(run => run.FilteredRows, progress.FilteredRows)
            .Set(run => run.DeduplicatedRows, progress.DeduplicatedRows)
            .Set(run => run.InsertedRows, progress.InsertedRows)
            .Set(run => run.UpdatedRows, progress.UpdatedRows);

        var result = await _collection.UpdateOneAsync(
            filter,
            update,
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
    }

    public async Task<bool> TryMarkTerminalAsync(
        Guid runId,
        EtlRunStatus status,
        DateTimeOffset completedAt,
        BatchExecutionProgress? finalProgress,
        string? systemError,
        string? errorReportPath,
        CancellationToken cancellationToken)
    {
        ValidateId(runId);
        ValidateTerminalStatus(status);

        var statusFilter = status == EtlRunStatus.Interrupted
            ? Builders<EtlRun>.Filter.In(
                run => run.Status,
                [EtlRunStatus.Queued, EtlRunStatus.Running])
            : Builders<EtlRun>.Filter.Eq(run => run.Status, EtlRunStatus.Running);

        var filters = new List<FilterDefinition<EtlRun>>
        {
            Builders<EtlRun>.Filter.Eq(run => run.Id, runId),
            statusFilter
        };
        var update = Builders<EtlRun>.Update
            .Set(run => run.Status, status)
            .Set(run => run.CompletedAt, completedAt)
            .Set(run => run.SystemError, systemError)
            .Set(run => run.ErrorReportPath, errorReportPath);

        if (finalProgress is not null)
        {
            filters.Add(Builders<EtlRun>.Filter.Lte(run => run.ProcessedRows, finalProgress.ProcessedRows));
            filters.Add(Builders<EtlRun>.Filter.Lte(run => run.ValidRows, finalProgress.ValidRows));
            filters.Add(Builders<EtlRun>.Filter.Lte(run => run.InvalidRows, finalProgress.InvalidRows));
            filters.Add(Builders<EtlRun>.Filter.Lte(run => run.FilteredRows, finalProgress.FilteredRows));
            filters.Add(Builders<EtlRun>.Filter.Lte(run => run.DeduplicatedRows, finalProgress.DeduplicatedRows));
            filters.Add(Builders<EtlRun>.Filter.Lte(run => run.InsertedRows, finalProgress.InsertedRows));
            filters.Add(Builders<EtlRun>.Filter.Lte(run => run.UpdatedRows, finalProgress.UpdatedRows));

            update = update
                .Set(run => run.ProcessedRows, finalProgress.ProcessedRows)
                .Set(run => run.ValidRows, finalProgress.ValidRows)
                .Set(run => run.InvalidRows, finalProgress.InvalidRows)
                .Set(run => run.FilteredRows, finalProgress.FilteredRows)
                .Set(run => run.DeduplicatedRows, finalProgress.DeduplicatedRows)
                .Set(run => run.InsertedRows, finalProgress.InsertedRows)
                .Set(run => run.UpdatedRows, finalProgress.UpdatedRows);
        }

        var result = await _collection.UpdateOneAsync(
            Builders<EtlRun>.Filter.And(filters),
            update,
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
    }

    private static bool IsDuplicateIdWrite(MongoWriteException exception)
    {
        var writeError = exception.WriteError;

        if (writeError.Category != ServerErrorCategory.DuplicateKey)
        {
            return false;
        }

        var details = writeError.Details;
        var hasIdKeyPattern = details is not null
            && details.TryGetValue("keyPattern", out var keyPattern)
            && keyPattern.BsonType == BsonType.Document
            && keyPattern.AsBsonDocument.Contains("_id");

        return hasIdKeyPattern
            || writeError.Message.Contains(
                " index: _id_ dup key:",
                StringComparison.Ordinal);
    }

    private static void ValidateRun(EtlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateId(run.Id);
    }

    private static void ValidateId(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("ETL run identifier cannot be empty.", nameof(runId));
        }
    }

    private static void ValidateTerminalStatus(EtlRunStatus status)
    {
        if (status is not EtlRunStatus.Completed
            and not EtlRunStatus.PartiallyCompleted
            and not EtlRunStatus.Failed
            and not EtlRunStatus.Interrupted)
        {
            throw new ArgumentException("ETL run status must be terminal.", nameof(status));
        }
    }
}
