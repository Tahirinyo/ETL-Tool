using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
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

            BsonClassMap.RegisterClassMap<PipelineDefinition>(classMap =>
            {
                classMap.AutoMap();
                classMap.MapIdMember(pipeline => pipeline.Id)
                    .SetSerializer(guidSerializer);
                classMap.MapMember(pipeline => pipeline.DestinationType)
                    .SetDefaultValue(DestinationType.MongoDb);
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

            _registered = true;
        }
    }
}
