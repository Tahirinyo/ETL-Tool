using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Uploads;

public interface IUploadValidationService
{
    Task<UploadValidationResult> StoreValidatedAsync(
        Stream content,
        string originalFileName,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken);
}
