using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public sealed class ConvertToDateTransformationHandler : ISourceDateFormatTransformationHandler
{
    public TransformationType Type => TransformationType.ConvertToDate;

    public TransformationResult Apply(DataRow row, TransformationRule rule) =>
        throw new InvalidOperationException(
            "The convert to date transformation requires the pipeline source culture and date format.");

    public TransformationResult Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture) =>
        Apply(row, rule, sourceCulture, dateFormat: null);

    public TransformationResult Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceCulture);

        if (string.IsNullOrWhiteSpace(rule.SourceField))
        {
            throw new InvalidOperationException(
                "The convert to date transformation requires a non-empty source field.");
        }

        if (!row.Values.TryGetValue(rule.SourceField, out var value))
        {
            throw new InvalidOperationException(
                $"The convert to date transformation field '{rule.SourceField}' is missing from row {row.SourceRowNumber}.");
        }

        if (value is null or DateTime)
        {
            return TransformationResult.Transformed(row);
        }

        if (value is not string text)
        {
            throw new InvalidOperationException(
                $"The convert to date transformation field '{rule.SourceField}' in row {row.SourceRowNumber} has unsupported value type '{value.GetType().FullName}'.");
        }

        row.Values[rule.SourceField] = DateTextParser.Parse(
            text,
            sourceCulture,
            dateFormat,
            $"The convert to date transformation field '{rule.SourceField}' in row {row.SourceRowNumber}");
        return TransformationResult.Transformed(row);
    }
}
