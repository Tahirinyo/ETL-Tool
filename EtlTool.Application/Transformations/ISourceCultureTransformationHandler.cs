using System.Globalization;
using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;

namespace EtlTool.Application.Transformations;

public interface ISourceCultureTransformationHandler : ITransformationHandler
{
    TransformationResult Apply(
        DataRow row,
        TransformationRule rule,
        CultureInfo sourceCulture);
}
