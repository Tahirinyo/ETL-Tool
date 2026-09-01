using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Connections;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace EtlTool.Infrastructure.MongoDB;

internal static class MongoBsonMappings
{
    private static readonly object Sync = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Sync)
        {
            if (_registered)
            {
                return;
            }

            var guidSerializer = new GuidSerializer(GuidRepresentation.Standard);
            var nullableGuidSerializer = new NullableSerializer<Guid>(guidSerializer);

            BsonClassMap.RegisterClassMap<PipelineDefinition>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapIdMember(pipeline => pipeline.Id)
                    .SetSerializer(guidSerializer);
                classMap.MapMember(pipeline => pipeline.DestinationType)
                    .SetDefaultValue(DestinationType.MongoDb);
                classMap.MapMember(pipeline => pipeline.MongoDbDestinationConnectionId)
                    .SetSerializer(nullableGuidSerializer);
            });
            BsonClassMap.RegisterClassMap<EtlRunExecutionConfiguration>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapMember(configuration => configuration.DestinationType)
                    .SetDefaultValue(DestinationType.MongoDb);
            });
            BsonClassMap.RegisterClassMap<EtlRun>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapIdMember(run => run.Id)
                    .SetSerializer(guidSerializer);
                classMap.MapMember(run => run.PipelineId)
                    .SetSerializer(guidSerializer);
            });
            BsonClassMap.RegisterClassMap<TransformationRule>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapIdMember(rule => rule.Id)
                    .SetSerializer(guidSerializer);
            });
            BsonClassMap.RegisterClassMap<ValidationRule>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapIdMember(rule => rule.Id)
                    .SetSerializer(guidSerializer);
            });
            BsonClassMap.RegisterClassMap<SavedDatabaseConnectionDocument>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapIdMember(connection => connection.Id)
                    .SetSerializer(guidSerializer);
            });
            BsonClassMap.RegisterClassMap<PostgreSqlSourceOptions>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapMember(value => value.SavedConnectionId)
                    .SetSerializer(nullableGuidSerializer);
            });
            BsonClassMap.RegisterClassMap<MongoDbSourceOptions>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapMember(value => value.SavedConnectionId)
                    .SetSerializer(nullableGuidSerializer);
            });
            BsonClassMap.RegisterClassMap<PostgreSqlDestinationOptions>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapMember(value => value.SavedConnectionId)
                    .SetSerializer(nullableGuidSerializer);
            });
            BsonClassMap.RegisterClassMap<SavedConnectionReference>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapMember(value => value.ConnectionId)
                    .SetSerializer(guidSerializer);
            });

            _registered = true;
        }
    }
}
