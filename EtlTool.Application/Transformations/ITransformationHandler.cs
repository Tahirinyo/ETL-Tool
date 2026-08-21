using EtlTool.Application.Extraction;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;

namespace EtlTool.Application.Transformations;

public interface ITransformationHandler
{
    TransformationType Type { get; }

    DataRow Apply(DataRow row, TransformationRule rule);
}
