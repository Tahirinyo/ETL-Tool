using System.Text.Json;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Transformations;

public sealed class TransformationRuleService : ITransformationRuleService
{
    private const string DefaultValueKey = "Value";
    private const string FindKey = "Find";
    private const string ReplaceKey = "Replace";
    private const string FilterOperatorKey = "Operator";
    private const string FilterValueKey = "Value";
    private const string DeduplicationFieldsKey = "Fields";

    private readonly IPipelineDefinitionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public TransformationRuleService(
        IPipelineDefinitionRepository repository,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async Task<TransformationRule?> CreateAsync(
        Guid pipelineId,
        TransformationRuleInput input,
        CancellationToken cancellationToken)
    {
        ValidatePipelineId(pipelineId);
        ArgumentNullException.ThrowIfNull(input);

        var pipeline = await _repository.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return null;

        ValidateExistingOrders(pipeline.TransformationRules);
        var rule = CreateRule(input, pipeline, NextOrder(pipeline.TransformationRules));
        var rules = CopyRules(pipeline.TransformationRules);
        rules.Add(rule);

        return await UpdatePipelineAsync(pipeline, rules, cancellationToken) ? rule : null;
    }

    public async Task<bool> UpdateAsync(
        Guid pipelineId,
        Guid ruleId,
        TransformationRuleInput input,
        CancellationToken cancellationToken)
    {
        ValidatePipelineId(pipelineId);
        ValidateRuleId(ruleId);
        ArgumentNullException.ThrowIfNull(input);

        var pipeline = await _repository.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return false;

        ValidateExistingOrders(pipeline.TransformationRules);
        var existing = pipeline.TransformationRules.SingleOrDefault(rule => rule.Id == ruleId);
        if (existing is null) return false;

        var updatedRule = CreateRule(input, pipeline, existing.Order);
        updatedRule.Id = existing.Id;
        var rules = CopyRules(pipeline.TransformationRules);
        rules[rules.FindIndex(rule => rule.Id == ruleId)] = updatedRule;

        return await UpdatePipelineAsync(pipeline, rules, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        Guid pipelineId,
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        ValidatePipelineId(pipelineId);
        ValidateRuleId(ruleId);

        var pipeline = await _repository.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return false;

        ValidateExistingOrders(pipeline.TransformationRules);
        var rules = CopyRules(pipeline.TransformationRules);
        if (rules.RemoveAll(rule => rule.Id == ruleId) == 0) return false;

        return await UpdatePipelineAsync(pipeline, rules, cancellationToken);
    }

    public async Task<bool> ReorderAsync(
        Guid pipelineId,
        IReadOnlyList<Guid> orderedRuleIds,
        CancellationToken cancellationToken)
    {
        ValidatePipelineId(pipelineId);
        ArgumentNullException.ThrowIfNull(orderedRuleIds);

        var pipeline = await _repository.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return false;

        ValidateExistingOrders(pipeline.TransformationRules);
        ValidateReorderRequest(orderedRuleIds, pipeline.TransformationRules);

        if (orderedRuleIds.Count < 2)
        {
            return true;
        }

        var rules = CopyRules(pipeline.TransformationRules);
        var rulesById = rules.ToDictionary(rule => rule.Id);
        for (var index = 0; index < orderedRuleIds.Count; index++)
        {
            rulesById[orderedRuleIds[index]].Order = index + 1;
        }

        return await UpdatePipelineAsync(pipeline, rules, cancellationToken);
    }

    private async Task<bool> UpdatePipelineAsync(
        PipelineDefinition pipeline,
        List<TransformationRule> rules,
        CancellationToken cancellationToken)
    {
        var replacement = new PipelineDefinition
        {
            Id = pipeline.Id,
            Name = pipeline.Name,
            Description = pipeline.Description,
            SourceType = pipeline.SourceType,
            SourceOptions = pipeline.SourceOptions,
            ExpectedSchema = pipeline.ExpectedSchema,
            FieldMappings = pipeline.FieldMappings,
            TransformationRules = rules,
            ValidationRules = pipeline.ValidationRules,
            DestinationType = pipeline.DestinationType,
            DestinationDatabase = pipeline.DestinationDatabase,
            DestinationCollection = pipeline.DestinationCollection,
            UpsertKeyField = pipeline.UpsertKeyField,
            CreatedAt = pipeline.CreatedAt,
            UpdatedAt = _timeProvider.GetUtcNow()
        };

        return await _repository.UpdateAsync(replacement, cancellationToken);
    }

    private static TransformationRule CreateRule(
        TransformationRuleInput input,
        PipelineDefinition pipeline,
        int order)
    {
        ValidateType(input.Type);

        var configuration = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceField = input.SourceField;

        if (input.Type == TransformationType.Deduplicate)
        {
            var selectedFields = ValidateDeduplicationFields(
                input.SelectedFields,
                pipeline.FieldMappings);
            configuration[DeduplicationFieldsKey] = JsonSerializer.Serialize(selectedFields);
            sourceField = null;
        }
        else
        {
            ValidateSourceField(input.SourceField, pipeline.FieldMappings);
        }

        switch (input.Type)
        {
            case TransformationType.SetDefaultValue:
                if (input.DefaultValue is null)
                {
                    throw new ArgumentException("A default value is required.", nameof(input));
                }
                configuration[DefaultValueKey] = input.DefaultValue;
                break;
            case TransformationType.FindAndReplace:
                if (string.IsNullOrEmpty(input.Find))
                {
                    throw new ArgumentException("A find value is required.", nameof(input));
                }
                if (input.Replace is null)
                {
                    throw new ArgumentException("A replace value is required.", nameof(input));
                }
                configuration[FindKey] = input.Find;
                configuration[ReplaceKey] = input.Replace;
                break;
            case TransformationType.FilterRow:
                ValidateFilterOperator(input.FilterOperator);
                if (input.FilterValue is null)
                {
                    throw new ArgumentException("A filter comparison value is required.", nameof(input));
                }
                configuration[FilterOperatorKey] = input.FilterOperator.ToString();
                configuration[FilterValueKey] = input.FilterValue;
                break;
        }

        return new TransformationRule
        {
            Id = Guid.NewGuid(),
            Type = input.Type,
            Order = order,
            SourceField = sourceField,
            Configuration = configuration
        };
    }

    private static void ValidateType(TransformationType type)
    {
        if (type is not TransformationType.Trim
            and not TransformationType.ToUpper
            and not TransformationType.ToLower
            and not TransformationType.ConvertToString
            and not TransformationType.ConvertToInteger
            and not TransformationType.ConvertToDecimal
            and not TransformationType.ConvertToDate
            and not TransformationType.FilterRow
            and not TransformationType.SetDefaultValue
            and not TransformationType.FindAndReplace
            and not TransformationType.Deduplicate)
        {
            throw new ArgumentException("The transformation type is not supported.", nameof(type));
        }
    }

    private static void ValidateSourceField(string? sourceField, IReadOnlyList<FieldMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(sourceField))
        {
            throw new ArgumentException("A source field is required.", nameof(sourceField));
        }

        if (mappings is null || !mappings.Any(mapping => mapping is not null
            && mapping.IsIncluded
            && string.Equals(mapping.TargetField, sourceField, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The source field must be an included mapped output field.", nameof(sourceField));
        }
    }

    private static void ValidateExistingOrders(IReadOnlyCollection<TransformationRule> rules)
    {
        if (rules is null || rules.Any(rule => rule is null || rule.Configuration is null))
        {
            throw new InvalidOperationException("The saved transformation rules are invalid.");
        }

        if (rules.Select(rule => rule.Order).Distinct().Count() != rules.Count)
        {
            throw new InvalidOperationException("The saved transformation rule orders are not unique.");
        }
    }

    private static int NextOrder(IReadOnlyCollection<TransformationRule> rules)
    {
        if (rules.Count == 0) return 1;
        var maximum = rules.Max(rule => rule.Order);
        if (maximum == int.MaxValue)
        {
            throw new InvalidOperationException("A new transformation rule order cannot be assigned.");
        }
        return maximum + 1;
    }

    private static string[] ValidateDeduplicationFields(
        IReadOnlyList<string>? selectedFields,
        IReadOnlyList<FieldMapping> mappings)
    {
        if (selectedFields is not { Count: > 0 })
        {
            throw new ArgumentException(
                "At least one deduplication field is required.",
                nameof(selectedFields));
        }

        var validatedFields = new string[selectedFields.Count];
        var uniqueFields = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < selectedFields.Count; index++)
        {
            var field = selectedFields[index];
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new ArgumentException(
                    "Deduplication field names cannot be empty or whitespace.",
                    nameof(selectedFields));
            }

            if (!uniqueFields.Add(field))
            {
                throw new ArgumentException(
                    $"Deduplication field '{field}' is selected more than once.",
                    nameof(selectedFields));
            }

            if (mappings is null || !mappings.Any(mapping => mapping is not null
                && mapping.IsIncluded
                && string.Equals(mapping.TargetField, field, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Deduplication field '{field}' must be an included mapped output field.",
                    nameof(selectedFields));
            }

            validatedFields[index] = field;
        }

        return validatedFields;
    }

    private static void ValidateFilterOperator(FilterOperator filterOperator)
    {
        if (!Enum.IsDefined(filterOperator) || filterOperator == FilterOperator.Unspecified)
        {
            throw new ArgumentException(
                "The filter operator is not supported.",
                nameof(filterOperator));
        }
    }

    private static void ValidateReorderRequest(
        IReadOnlyList<Guid> orderedRuleIds,
        IReadOnlyCollection<TransformationRule> savedRules)
    {
        if (orderedRuleIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Transformation rule identifiers cannot be empty.", nameof(orderedRuleIds));
        }

        if (orderedRuleIds.Count != savedRules.Count
            || orderedRuleIds.Distinct().Count() != orderedRuleIds.Count
            || !orderedRuleIds.All(id => savedRules.Any(rule => rule.Id == id)))
        {
            throw new ArgumentException(
                "The transformation rule order must contain each rule in this pipeline exactly once.",
                nameof(orderedRuleIds));
        }
    }

    private static List<TransformationRule> CopyRules(IEnumerable<TransformationRule> rules) => rules
        .Select(rule => new TransformationRule
        {
            Id = rule.Id,
            Type = rule.Type,
            Order = rule.Order,
            SourceField = rule.SourceField,
            Configuration = new Dictionary<string, string>(rule.Configuration, StringComparer.Ordinal)
        })
        .ToList();

    private static void ValidatePipelineId(Guid pipelineId)
    {
        if (pipelineId == Guid.Empty) throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(pipelineId));
    }

    private static void ValidateRuleId(Guid ruleId)
    {
        if (ruleId == Guid.Empty) throw new ArgumentException("Transformation rule identifier cannot be empty.", nameof(ruleId));
    }
}
