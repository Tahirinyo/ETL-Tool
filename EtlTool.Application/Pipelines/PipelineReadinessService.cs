using System.Globalization;
using EtlTool.Application.Mapping;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Pipelines;

public sealed class PipelineReadinessService : IPipelineReadinessService
{
    private const string SourceComponent = "Source";
    private const string MappingComponent = "Mapping";
    private const string TransformationComponent = "Transformation";
    private const string ValidationComponent = "Validation";
    private const string DestinationComponent = "Destination";
    private const string UpsertKeyComponent = "Upsert key";

    private readonly IPipelineDefinitionRepository _repository;
    private readonly FieldMappingService _fieldMappingService;

    public PipelineReadinessService(
        IPipelineDefinitionRepository repository,
        FieldMappingService fieldMappingService)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(fieldMappingService);

        _repository = repository;
        _fieldMappingService = fieldMappingService;
    }

    public async Task<PipelineReadinessResult?> EvaluateAsync(
        Guid pipelineId,
        CancellationToken cancellationToken)
    {
        if (pipelineId == Guid.Empty)
        {
            throw new ArgumentException("Pipeline identifier cannot be empty.", nameof(pipelineId));
        }

        var pipeline = await _repository.GetByIdAsync(pipelineId, cancellationToken);
        if (pipeline is null)
        {
            return null;
        }

        return Evaluate(pipeline);
    }

    private PipelineReadinessResult Evaluate(PipelineDefinition pipeline)
    {
        var problems = new List<PipelineReadinessProblem>();
        var sourceState = EvaluateSourceAndSchema(pipeline, problems);
        var mappingState = EvaluateMappings(pipeline, sourceState.HasUsableSchema, problems);

        EvaluateTransformations(pipeline, mappingState, problems);
        EvaluateValidations(pipeline, mappingState, sourceState.Culture, problems);
        EvaluateDestination(pipeline, problems);
        EvaluateUpsertKey(pipeline, mappingState, problems);

        return new PipelineReadinessResult(problems);
    }

    private static SourceState EvaluateSourceAndSchema(
        PipelineDefinition pipeline,
        List<PipelineReadinessProblem> problems)
    {
        CultureInfo? sourceCulture = null;
        var sourceOptions = pipeline.SourceOptions;

        if (pipeline.SourceType is not SourceType.Csv and not SourceType.Xlsx)
        {
            AddProblem(problems, SourceComponent, "The pipeline source type must be CSV or XLSX.");
        }

        if (sourceOptions is null)
        {
            AddProblem(problems, SourceComponent, "The pipeline source options are missing.");
        }
        else
        {
            try
            {
                sourceCulture = sourceOptions.ResolveCulture();
            }
            catch (CultureNotFoundException exception)
            {
                AddProblem(problems, SourceComponent, exception.Message);
            }

            if (!sourceOptions.FirstRowIsHeader)
            {
                AddProblem(problems, SourceComponent, "The configured source must have a header row.");
            }

            if (pipeline.SourceType == SourceType.Csv
                && sourceOptions.Delimiter is not null
                && sourceOptions.Delimiter is not CsvDelimiter.Comma
                    and not CsvDelimiter.Semicolon
                    and not CsvDelimiter.Tab)
            {
                AddProblem(problems, SourceComponent, "The CSV delimiter is not supported.");
            }

            if (pipeline.SourceType == SourceType.Xlsx
                && string.IsNullOrWhiteSpace(sourceOptions.WorksheetName))
            {
                AddProblem(problems, SourceComponent, "An XLSX worksheet name must be configured.");
            }

            if (sourceCulture is not null)
            {
                AddConfigurationProblem(
                    problems,
                    SourceComponent,
                    () => DateTextParser.ValidateFormat(sourceOptions.DateFormat, sourceCulture));
            }
        }

        var hasUsableSchema = true;
        try
        {
            FieldMappingService.ValidateExpectedSchema(pipeline.ExpectedSchema);
        }
        catch (InvalidOperationException exception)
        {
            AddProblem(problems, SourceComponent, exception.Message);
            hasUsableSchema = false;
        }

        return new SourceState(sourceCulture, hasUsableSchema);
    }

    private MappingState EvaluateMappings(
        PipelineDefinition pipeline,
        bool hasUsableSchema,
        List<PipelineReadinessProblem> problems)
    {
        var mappings = pipeline.FieldMappings;
        if (mappings is null)
        {
            AddProblem(problems, MappingComponent, "The field mapping collection is missing.");
            return MappingState.Unavailable;
        }

        if (!hasUsableSchema)
        {
            return MappingState.Unavailable;
        }

        try
        {
            _ = _fieldMappingService.Prepare(pipeline);
        }
        catch (InvalidOperationException exception)
        {
            AddProblem(problems, MappingComponent, exception.Message);
        }

        var activeTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (mapping is not null
                && mapping.IsIncluded
                && !string.IsNullOrWhiteSpace(mapping.TargetField))
            {
                activeTargets.Add(mapping.TargetField);
            }
        }

        return new MappingState(activeTargets);
    }

    private static void EvaluateTransformations(
        PipelineDefinition pipeline,
        MappingState mappingState,
        List<PipelineReadinessProblem> problems)
    {
        if (pipeline.TransformationRules is null)
        {
            AddProblem(problems, TransformationComponent, "The transformation rule collection is missing.");
            return;
        }

        var indexedRules = pipeline.TransformationRules
            .Select((rule, index) => new IndexedTransformationRule(rule, index))
            .OrderBy(entry => entry.Rule?.Order ?? int.MaxValue)
            .ThenBy(entry => entry.Index)
            .ToArray();
        var orders = new HashSet<int>();

        foreach (var entry in indexedRules)
        {
            var rule = entry.Rule;
            if (rule is null)
            {
                AddProblem(problems, TransformationComponent, "The transformation rule collection contains an invalid rule.");
                continue;
            }

            if (!orders.Add(rule.Order))
            {
                AddProblem(problems, TransformationComponent,
                    $"Transformation rule order '{rule.Order}' is used by more than one rule.");
            }

            if (!IsSupported(rule.Type))
            {
                AddProblem(problems, TransformationComponent,
                    $"The transformation type '{rule.Type}' is not supported.");
                continue;
            }

            if (rule.Configuration is null)
            {
                AddProblem(problems, TransformationComponent,
                    "The transformation rule configuration collection is missing.");
            }

            if (rule.Type == TransformationType.Deduplicate)
            {
                EvaluateDeduplication(rule, mappingState, problems);
                continue;
            }

            ValidateMappedField(
                rule.SourceField,
                "The transformation source field",
                TransformationComponent,
                mappingState,
                problems);
            EvaluateTransformationConfiguration(rule, problems);
        }
    }

    private static void EvaluateDeduplication(
        TransformationRule rule,
        MappingState mappingState,
        List<PipelineReadinessProblem> problems)
    {
        if (rule.Configuration is null)
        {
            return;
        }

        IReadOnlyList<string>? fields = null;
        AddConfigurationProblem(
            problems,
            TransformationComponent,
            () => fields = DeduplicateTransformationHandler.ReadSelectedFields(rule));

        if (fields is null || !mappingState.CanResolveTargets)
        {
            return;
        }

        foreach (var field in fields)
        {
            ValidateMappedField(
                field,
                "The deduplication transformation field",
                TransformationComponent,
                mappingState,
                problems);
        }
    }

    private static void EvaluateTransformationConfiguration(
        TransformationRule rule,
        List<PipelineReadinessProblem> problems)
    {
        if (rule.Configuration is null)
        {
            return;
        }

        switch (rule.Type)
        {
            case TransformationType.SetDefaultValue:
                AddConfigurationProblem(
                    problems,
                    TransformationComponent,
                    () => _ = DefaultValueTransformationHandler.ReadDefaultValue(rule));
                break;
            case TransformationType.FindAndReplace:
                AddConfigurationProblem(
                    problems,
                    TransformationComponent,
                    () => _ = FindAndReplaceTransformationHandler.ReadConfiguration(rule));
                break;
            case TransformationType.FilterRow:
                AddConfigurationProblem(
                    problems,
                    TransformationComponent,
                    () => _ = ConditionalFilterTransformationHandler.ReadOperator(rule));
                AddConfigurationProblem(
                    problems,
                    TransformationComponent,
                    () => _ = ConditionalFilterTransformationHandler.ReadComparisonValue(rule));
                break;
        }
    }

    private static void EvaluateValidations(
        PipelineDefinition pipeline,
        MappingState mappingState,
        CultureInfo? sourceCulture,
        List<PipelineReadinessProblem> problems)
    {
        if (pipeline.ValidationRules is null)
        {
            AddProblem(problems, ValidationComponent, "The validation rule collection is missing.");
            return;
        }

        foreach (var rule in pipeline.ValidationRules)
        {
            if (rule is null)
            {
                AddProblem(problems, ValidationComponent, "The validation rule collection contains an invalid rule.");
                continue;
            }

            if (!IsSupported(rule.Type))
            {
                AddProblem(problems, ValidationComponent,
                    $"The validation type '{rule.Type}' is not supported.");
                continue;
            }

            if (rule.Configuration is null)
            {
                AddProblem(problems, ValidationComponent,
                    "The validation rule configuration collection is missing.");
            }

            ValidateMappedField(
                rule.Field,
                "The validation field",
                ValidationComponent,
                mappingState,
                problems);

            if (rule.Type == ValidationType.UpsertKeyRequired
                && !string.IsNullOrWhiteSpace(rule.Field)
                && !string.Equals(rule.Field, pipeline.UpsertKeyField, StringComparison.Ordinal))
            {
                AddProblem(problems, ValidationComponent,
                    "The upsert-key validation rule must target the current pipeline upsert-key field.");
            }

            if (rule.Configuration is null)
            {
                continue;
            }

            switch (rule.Type)
            {
                case ValidationType.NumericRange when sourceCulture is not null:
                    AddConfigurationProblem(
                        problems,
                        ValidationComponent,
                        () => NumericRangeValidationHandler.ValidateConfiguration(rule, sourceCulture));
                    break;
                case ValidationType.TextLengthRange:
                    AddConfigurationProblem(
                        problems,
                        ValidationComponent,
                        () => TextLengthValidationHandler.ValidateConfiguration(rule));
                    break;
                case ValidationType.DateRange when sourceCulture is not null:
                    AddConfigurationProblem(
                        problems,
                        ValidationComponent,
                        () => DateRangeValidationHandler.ValidateConfiguration(
                            rule,
                            sourceCulture,
                            pipeline.SourceOptions?.DateFormat));
                    break;
            }
        }
    }

    private static void EvaluateDestination(
        PipelineDefinition pipeline,
        List<PipelineReadinessProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(pipeline.DestinationDatabase))
        {
            AddProblem(problems, DestinationComponent,
                "The destination database must be configured.");
        }

        if (string.IsNullOrWhiteSpace(pipeline.DestinationCollection))
        {
            AddProblem(problems, DestinationComponent,
                "The destination collection must be configured.");
        }
    }

    private static void EvaluateUpsertKey(
        PipelineDefinition pipeline,
        MappingState mappingState,
        List<PipelineReadinessProblem> problems)
    {
        ValidateMappedField(
            pipeline.UpsertKeyField,
            "The pipeline upsert-key field",
            UpsertKeyComponent,
            mappingState,
            problems);
    }

    private static void ValidateMappedField(
        string? field,
        string subject,
        string component,
        MappingState mappingState,
        List<PipelineReadinessProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            AddProblem(problems, component, $"{subject} must be an included mapped output field.");
            return;
        }

        if (mappingState.CanResolveTargets && !mappingState.ActiveTargets.Contains(field))
        {
            AddProblem(problems, component, $"{subject} '{field}' is not an included mapped output field.");
        }
    }

    private static void AddConfigurationProblem(
        List<PipelineReadinessProblem> problems,
        string component,
        Action validate)
    {
        try
        {
            validate();
        }
        catch (InvalidOperationException exception)
        {
            AddProblem(problems, component, exception.Message);
        }
        catch (FormatException exception)
        {
            AddProblem(problems, component, exception.Message);
        }
    }

    private static void AddProblem(
        List<PipelineReadinessProblem> problems,
        string component,
        string message) => problems.Add(new PipelineReadinessProblem(component, message));

    private static bool IsSupported(TransformationType type) => type is
        TransformationType.Trim
        or TransformationType.ToUpper
        or TransformationType.ToLower
        or TransformationType.ConvertToString
        or TransformationType.ConvertToInteger
        or TransformationType.ConvertToDecimal
        or TransformationType.ConvertToDate
        or TransformationType.FilterRow
        or TransformationType.SetDefaultValue
        or TransformationType.FindAndReplace
        or TransformationType.Deduplicate;

    private static bool IsSupported(ValidationType type) => type is
        ValidationType.Required
        or ValidationType.EmailFormat
        or ValidationType.NumericRange
        or ValidationType.TextLengthRange
        or ValidationType.DateRange
        or ValidationType.UpsertKeyRequired;

    private sealed record MappingState(IReadOnlySet<string> ActiveTargets)
    {
        public static MappingState Unavailable { get; } = new(new HashSet<string>(StringComparer.Ordinal))
        {
            CanResolveTargets = false
        };

        public bool CanResolveTargets { get; init; } = true;
    }

    private sealed record SourceState(CultureInfo? Culture, bool HasUsableSchema);

    private sealed record IndexedTransformationRule(TransformationRule? Rule, int Index);
}
