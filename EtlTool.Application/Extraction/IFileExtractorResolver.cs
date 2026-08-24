using EtlTool.Domain.Enums;

namespace EtlTool.Application.Extraction;

public interface IFileExtractorResolver
{
    IFileExtractor Resolve(SourceType sourceType);
}
