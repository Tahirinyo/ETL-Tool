using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Transformations;

public interface ISourceCultureTransformationHandler : ITransformationHandler
{
    DataRow Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture);
}
