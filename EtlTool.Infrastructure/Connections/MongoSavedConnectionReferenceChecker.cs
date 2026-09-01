using EtlTool.Application.Connections;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Driver;

namespace EtlTool.Infrastructure.Connections;

public sealed class MongoSavedConnectionReferenceChecker : ISavedConnectionReferenceChecker
{
    private readonly MongoMetadataDatabase _metadataDatabase;

    public MongoSavedConnectionReferenceChecker(MongoMetadataDatabase metadataDatabase)
    {
        ArgumentNullException.ThrowIfNull(metadataDatabase);
        _metadataDatabase = metadataDatabase;
    }

    public async Task<bool> IsReferencedAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("Connection identifier cannot be empty.", nameof(connectionId));
        }

        var pipelineFilter = Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Or(
            Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.And(
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.SourceType, SourceType.PostgreSql),
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.PostgreSqlSource!.SavedConnectionId, connectionId)),
            Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.And(
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.SourceType, SourceType.MongoDb),
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.MongoDbSource!.SavedConnectionId, connectionId)),
            Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.And(
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.DestinationType, DestinationType.PostgreSql),
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.PostgreSqlDestination!.SavedConnectionId, connectionId)),
            Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.And(
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.DestinationType, DestinationType.MongoDb),
                Builders<EtlTool.Domain.Entities.PipelineDefinition>.Filter.Eq(
                    pipeline => pipeline.MongoDbDestinationConnectionId, connectionId)));
        if (await _metadataDatabase.PipelineDefinitions.Find(pipelineFilter)
                .Limit(1).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var activeRunFilter = Builders<EtlTool.Domain.Entities.EtlRun>.Filter.And(
            Builders<EtlTool.Domain.Entities.EtlRun>.Filter.In(
                run => run.Status,
                [EtlRunStatus.Queued, EtlRunStatus.Running]),
            Builders<EtlTool.Domain.Entities.EtlRun>.Filter.Or(
                Builders<EtlTool.Domain.Entities.EtlRun>.Filter.Eq(
                    run => run.ExecutionConfiguration!.SourceConnection!.ConnectionId, connectionId),
                Builders<EtlTool.Domain.Entities.EtlRun>.Filter.Eq(
                    run => run.ExecutionConfiguration!.DestinationConnection!.ConnectionId, connectionId)));
        return await _metadataDatabase.EtlRuns.Find(activeRunFilter)
            .Limit(1).AnyAsync(cancellationToken).ConfigureAwait(false);
    }
}
