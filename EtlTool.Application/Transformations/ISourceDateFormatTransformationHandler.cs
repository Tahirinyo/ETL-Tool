using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Transformations;

public interface ISourceDateFormatTransformationHandler : ISourceCultureTransformationHandler
{
    DataRow Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture,
        string? dateFormat);
}
