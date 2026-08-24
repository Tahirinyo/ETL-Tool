using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Processing;

public sealed class PipelineRowProcessor
{
    private readonly FieldMappingService _fieldMappingService;
    private readonly TransformationEngine _transformationEngine;
    private readonly ValidationEngine _validationEngine;

    public PipelineRowProcessor(
        FieldMappingService fieldMappingService,
        TransformationEngine transformationEngine,
        ValidationEngine validationEngine)
    {
        ArgumentNullException.ThrowIfNull(fieldMappingService);
        ArgumentNullException.ThrowIfNull(transformationEngine);
        ArgumentNullException.ThrowIfNull(validationEngine);

        _fieldMappingService = fieldMappingService;
        _transformationEngine = transformationEngine;
        _validationEngine = validationEngine;
    }

    public PipelineRowProcessingSession CreateSession(PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var sourceOptions = CopySourceOptions(
            pipeline.SourceOptions
                ?? throw new InvalidOperationException("The pipeline source options are missing."));
        var transformationRules = pipeline.TransformationRules
            ?? throw new InvalidOperationException("The transformation rule collection is missing.");
        var validationRules = pipeline.ValidationRules
            ?? throw new InvalidOperationException("The validation rule collection is missing.");

        return new PipelineRowProcessingSession(
            _fieldMappingService,
            _fieldMappingService.Prepare(pipeline),
            _transformationEngine.CreateExecution(transformationRules, sourceOptions),
            _validationEngine,
            validationRules.ToArray(),
            sourceOptions);
    }

    private static SourceOptions CopySourceOptions(SourceOptions sourceOptions) => new()
    {
        CultureName = sourceOptions.CultureName,
        DateFormat = sourceOptions.DateFormat,
        Delimiter = sourceOptions.Delimiter,
        WorksheetName = sourceOptions.WorksheetName,
        FirstRowIsHeader = sourceOptions.FirstRowIsHeader
    };
}

public sealed class PipelineRowProcessingSession
{
    private readonly FieldMappingService _fieldMappingService;
    private readonly FieldMappingPlan _mappingPlan;
    private readonly TransformationExecution _transformationExecution;
    private readonly ValidationEngine _validationEngine;
    private readonly IReadOnlyList<ValidationRule> _validationRules;
    private readonly SourceOptions _sourceOptions;

    internal PipelineRowProcessingSession(
        FieldMappingService fieldMappingService,
        FieldMappingPlan mappingPlan,
        TransformationExecution transformationExecution,
        ValidationEngine validationEngine,
        IReadOnlyList<ValidationRule> validationRules,
        SourceOptions sourceOptions)
    {
        _fieldMappingService = fieldMappingService;
        _mappingPlan = mappingPlan;
        _transformationExecution = transformationExecution;
        _validationEngine = validationEngine;
        _validationRules = validationRules;
        _sourceOptions = sourceOptions;
    }

    public RowProcessingResult Process(DataRow sourceRow)
    {
        ArgumentNullException.ThrowIfNull(sourceRow);

        var mappedRow = _fieldMappingService.Apply(sourceRow, _mappingPlan);
        var transformation = _transformationExecution.ApplyForRowProcessing(mappedRow);

        if (!transformation.IsSuccess)
        {
            var failedRule = transformation.FailedRule!;
            var failure = transformation.Failure!;
            return RowProcessingResult.Invalid(
                transformation.Row,
                [
                    new RowProcessingError(
                        RowProcessingErrorStage.Transformation,
                        failedRule.SourceField,
                        failure.Message,
                        failedRule.Id,
                        failedRule.Type)
                ]);
        }

        var transformed = transformation.Result!;
        if (transformed.IsFiltered)
        {
            return RowProcessingResult.Filtered(transformed.Row);
        }

        if (transformed.IsDuplicate)
        {
            return RowProcessingResult.Duplicate(transformed.Row);
        }

        var validation = _validationEngine.Validate(
            transformed.Row,
            _validationRules,
            _sourceOptions);

        if (validation.IsValid)
        {
            return RowProcessingResult.Valid(validation.Row);
        }

        var errors = validation.Errors
            .Select(error => new RowProcessingError(
                RowProcessingErrorStage.Validation,
                error.Field,
                error.Message))
            .ToArray();

        return RowProcessingResult.Invalid(validation.Row, errors);
    }
}
