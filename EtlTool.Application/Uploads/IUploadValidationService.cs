using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Application.Uploads;

public interface IUploadValidationService
{
    Task<XlsxWorksheetStageResult> StoreForWorksheetSelectionAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken);

    Task<UploadValidationResult> ValidateStoredAsync(
        StoredUpload upload,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken);

    Task<UploadValidationResult> StoreValidatedAsync(
        Stream content,
        string originalFileName,
        SourceType sourceType,
        SourceOptions sourceOptions,
        CancellationToken cancellationToken);
}
