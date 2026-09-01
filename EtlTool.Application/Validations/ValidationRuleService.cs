using System.Globalization;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Validations;

public sealed class ValidationRuleService : IValidationRuleService
{
    private const string MinimumKey = "Minimum";
    private const string MaximumKey = "Maximum";

    private readonly IPipelineDefinitionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ValidationRuleService(IPipelineDefinitionRepository repository, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async Task<ValidationRule?> CreateAsync(
        Guid pipelineId,
        ValidationRuleInput input,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(pipelineId));
        }
        ArgumentNullException.ThrowIfNull(input);

        var pipeline = await _repository.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return null;

        var rule = CreateRule(input, pipeline);
        var rules = CopyRules(pipeline.ValidationRules);
        rules.Add(rule);

        return await UpdatePipelineAsync(pipeline, rules, cancellationToken) ? rule : null;
    }

    public async Task<bool> UpdateAsync(
        Guid pipelineId,
        Guid ruleId,
        ValidationRuleInput input,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(pipelineId));
        }
        if (ruleId == Guid.Empty)
        {
            throw new ArgumentException("Validation rule identifier cannot be empty.", nameof(ruleId));
        }
        ArgumentNullException.ThrowIfNull(input);

        var pipeline = await _repository.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null) return false;

        var index = pipeline.ValidationRules.FindIndex(rule => rule.Id == ruleId);
        if (index < 0) return false;

        var updatedRule = CreateRule(input, pipeline);
        updatedRule.Id = ruleId;
        var rules = CopyRules(pipeline.ValidationRules);
        rules[index] = updatedRule;

        return await UpdatePipelineAsync(pipeline, rules, cancellationToken);
    }

    private async Task<bool> UpdatePipelineAsync(
        PipelineDefinition pipeline,
        List<ValidationRule> rules,
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
            TransformationRules = pipeline.TransformationRules,
            ValidationRules = rules,
            DestinationType = pipeline.DestinationType,
            DestinationDatabase = pipeline.DestinationDatabase,
            DestinationCollection = pipeline.DestinationCollection,
            UpsertKeyField = pipeline.UpsertKeyField,
            CreatedAt = pipeline.CreatedAt,
            UpdatedAt = _timeProvider.GetUtcNow()
        };

        return await _repository.UpdateAsync(replacement, cancellationToken);
    }

    private static ValidationRule CreateRule(ValidationRuleInput input, PipelineDefinition pipeline)
    {
        ValidateType(input.Type);
        var field = input.Type == ValidationType.UpsertKeyRequired
            ? ValidateUpsertKeyField(pipeline)
            : ValidateMappedField(input.Field, pipeline.FieldMappings);
        var configuration = CreateConfiguration(input, pipeline.SourceOptions);

        return new ValidationRule
        {
            Id = Guid.NewGuid(),
            Type = input.Type,
            Field = field,
            Configuration = configuration,
            ErrorMessage = input.ErrorMessage ?? string.Empty
        };
    }

    private static Dictionary<string, string> CreateConfiguration(
        ValidationRuleInput input,
        SourceOptions sourceOptions)
    {
        return input.Type switch
        {
            ValidationType.NumericRange => CreateNumericRange(input, sourceOptions.ResolveCulture()),
            ValidationType.TextLengthRange => CreateTextLengthRange(input),
            ValidationType.DateRange => CreateDateRange(input, sourceOptions.ResolveCulture(), sourceOptions.DateFormat),
            _ => new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private static Dictionary<string, string> CreateNumericRange(ValidationRuleInput input, CultureInfo culture)
    {
        var minimum = ParseDecimalBound(input.Minimum, MinimumKey, culture);
        var maximum = ParseDecimalBound(input.Maximum, MaximumKey, culture);
        if (minimum is null && maximum is null) throw new ArgumentException("A minimum or maximum value is required.", nameof(input));
        if (minimum > maximum) throw new ArgumentException("Minimum cannot be greater than maximum.", nameof(input));
        return CreateRangeConfiguration(input.Minimum, input.Maximum);
    }

    private static Dictionary<string, string> CreateTextLengthRange(ValidationRuleInput input)
    {
        var minimum = ParseLengthBound(input.Minimum, MinimumKey);
        var maximum = ParseLengthBound(input.Maximum, MaximumKey);
        if (minimum is null && maximum is null) throw new ArgumentException("A minimum or maximum value is required.", nameof(input));
        if (minimum > maximum) throw new ArgumentException("Minimum cannot be greater than maximum.", nameof(input));
        return CreateRangeConfiguration(input.Minimum, input.Maximum);
    }

    private static Dictionary<string, string> CreateDateRange(ValidationRuleInput input, CultureInfo culture, string? dateFormat)
    {
        var minimum = ParseDateBound(input.Minimum, MinimumKey, culture, dateFormat);
        var maximum = ParseDateBound(input.Maximum, MaximumKey, culture, dateFormat);
        if (minimum is null && maximum is null) throw new ArgumentException("A minimum or maximum value is required.", nameof(input));
        if (minimum > maximum) throw new ArgumentException("Minimum cannot be later than maximum.", nameof(input));
        return CreateRangeConfiguration(input.Minimum, input.Maximum);
    }

    private static decimal? ParseDecimalBound(string? value, string name, CultureInfo culture)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} must not be empty when supplied.", nameof(value));
        try { return ExactNumericText.Parse(value, culture).ToDecimal(); }
        catch (FormatException exception) { throw new ArgumentException($"{name} must be a valid decimal value for culture '{culture.Name}'.", nameof(value), exception); }
        catch (OverflowException exception) { throw new ArgumentException($"{name} cannot be represented exactly as a Decimal value.", nameof(value), exception); }
    }

    private static int? ParseLengthBound(string? value, string name)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            throw new ArgumentException($"{name} must be a non-negative whole number.", nameof(value));
        }
        return parsed;
    }

    private static DateTime? ParseDateBound(string? value, string name, CultureInfo culture, string? dateFormat)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} must not be empty when supplied.", nameof(value));
        try { return DateTextParser.Parse(value, culture, dateFormat, $"The date range validation '{name}' configuration value"); }
        catch (FormatException exception) { throw new ArgumentException(exception.Message, nameof(value), exception); }
    }

    private static Dictionary<string, string> CreateRangeConfiguration(string? minimum, string? maximum)
    {
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal);
        if (minimum is not null) configuration[MinimumKey] = minimum;
        if (maximum is not null) configuration[MaximumKey] = maximum;
        return configuration;
    }

    private static void ValidateType(ValidationType type)
    {
        if (type is not ValidationType.Required
            and not ValidationType.EmailFormat
            and not ValidationType.NumericRange
            and not ValidationType.TextLengthRange
            and not ValidationType.DateRange
            and not ValidationType.UpsertKeyRequired)
        {
            throw new ArgumentException("The validation type is not supported.", nameof(type));
        }
    }

    private static string ValidateMappedField(string? field, IReadOnlyList<FieldMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("A mapped output field is required.", nameof(field));
        if (mappings is null || !mappings.Any(mapping => mapping is not null
            && mapping.IsIncluded
            && string.Equals(mapping.TargetField, field, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The field must be an included mapped output field.", nameof(field));
        }
        return field;
    }

    private static string ValidateUpsertKeyField(PipelineDefinition pipeline)
    {
        if (string.IsNullOrWhiteSpace(pipeline.UpsertKeyField))
        {
            throw new InvalidOperationException("Configure the pipeline upsert-key field before adding an upsert-key validation rule.");
        }
        return ValidateMappedField(pipeline.UpsertKeyField, pipeline.FieldMappings);
    }

    private static List<ValidationRule> CopyRules(IEnumerable<ValidationRule> rules) => rules
        .Select(rule => new ValidationRule
        {
            Id = rule.Id,
            Type = rule.Type,
            Field = rule.Field,
            Configuration = new Dictionary<string, string>(rule.Configuration, StringComparer.Ordinal),
            ErrorMessage = rule.ErrorMessage
        })
        .ToList();
}
