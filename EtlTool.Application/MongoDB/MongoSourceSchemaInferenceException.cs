namespace EtlTool.Application.MongoDB;

public sealed class MongoSourceSchemaInferenceException : Exception
{
    private MongoSourceSchemaInferenceException(
        string message,
        string? fieldName,
        IReadOnlyList<string> observedTypes)
        : base(message)
    {
        FieldName = fieldName;
        ObservedTypes = observedTypes;
    }

    public string? FieldName { get; }

    public IReadOnlyList<string> ObservedTypes { get; }

    public static MongoSourceSchemaInferenceException NoSchemaEvidence() => new(
        "The selected MongoDB collection contains no supported typed fields in the schema sample.",
        fieldName: null,
        observedTypes: []);

    public static MongoSourceSchemaInferenceException InvalidFieldName() => new(
        "The MongoDB source schema contains a field with an empty name.",
        fieldName: null,
        observedTypes: []);

    public static MongoSourceSchemaInferenceException Unsupported(
        string fieldName,
        string observedType) => new(
            $"MongoDB source field '{fieldName}' contains unsupported BSON type '{observedType}'.",
            fieldName,
            [observedType]);

    public static MongoSourceSchemaInferenceException Conflict(
        string fieldName,
        string firstObservedType,
        string secondObservedType) => new(
            $"MongoDB source field '{fieldName}' contains incompatible sampled types " +
            $"'{firstObservedType}' and '{secondObservedType}'.",
            fieldName,
            [firstObservedType, secondObservedType]);
}
