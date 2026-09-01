using EtlTool.Application.MongoDB;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using MongoDB.Bson;

namespace EtlTool.Infrastructure.MongoDB;

internal sealed class MongoSourceSchemaAccumulator
{
    private readonly SortedDictionary<string, ObservedType> _observations =
        new(StringComparer.Ordinal);

    public void Observe(BsonDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var element in document)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(element.Name))
            {
                throw MongoSourceSchemaInferenceException.InvalidFieldName();
            }

            var observedType = Classify(element.Name, element.Value);
            if (observedType is null)
            {
                continue;
            }

            if (!_observations.TryGetValue(element.Name, out var current))
            {
                _observations.Add(element.Name, observedType.Value);
                continue;
            }

            _observations[element.Name] = Reconcile(
                element.Name,
                current,
                observedType.Value);
        }
    }

    public IReadOnlyList<SourceFieldDefinition> Build()
    {
        if (_observations.Count == 0)
        {
            throw MongoSourceSchemaInferenceException.NoSchemaEvidence();
        }

        return _observations
            .Select(observation => new SourceFieldDefinition
            {
                Name = observation.Key,
                DataType = ToSourceFieldType(observation.Value)
            })
            .ToArray();
    }

    private static ObservedType? Classify(string fieldName, BsonValue value) => value switch
    {
        BsonNull => null,
        BsonString => ObservedType.String,
        BsonObjectId => ObservedType.ObjectId,
        BsonInt32 or BsonInt64 => ObservedType.Integer,
        BsonDouble or BsonDecimal128 => ObservedType.Decimal,
        BsonBoolean => ObservedType.Boolean,
        BsonDateTime => ObservedType.Date,
        _ => throw MongoSourceSchemaInferenceException.Unsupported(
            fieldName,
            value.BsonType.ToString())
    };

    private static ObservedType Reconcile(
        string fieldName,
        ObservedType current,
        ObservedType evidence)
    {
        if (current == evidence)
        {
            return current;
        }

        if (current is ObservedType.Integer or ObservedType.Decimal
            && evidence is ObservedType.Integer or ObservedType.Decimal)
        {
            return ObservedType.Decimal;
        }

        throw MongoSourceSchemaInferenceException.Conflict(
            fieldName,
            ToDisplayName(current),
            ToDisplayName(evidence));
    }

    private static SourceFieldType ToSourceFieldType(ObservedType observedType) => observedType switch
    {
        ObservedType.String or ObservedType.ObjectId => SourceFieldType.String,
        ObservedType.Integer => SourceFieldType.Integer,
        ObservedType.Decimal => SourceFieldType.Decimal,
        ObservedType.Boolean => SourceFieldType.Boolean,
        ObservedType.Date => SourceFieldType.Date,
        _ => throw new ArgumentOutOfRangeException(nameof(observedType), observedType, null)
    };

    private static string ToDisplayName(ObservedType observedType) => observedType switch
    {
        ObservedType.ObjectId => "ObjectId",
        _ => observedType.ToString()
    };

    private enum ObservedType
    {
        String,
        ObjectId,
        Integer,
        Decimal,
        Boolean,
        Date
    }
}
