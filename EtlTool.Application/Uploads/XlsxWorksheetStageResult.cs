namespace EtlTool.Application.Uploads;

public sealed class XlsxWorksheetStageResult
{
    private XlsxWorksheetStageResult(
        StoredUpload? upload,
        IReadOnlyList<string>? worksheetNames,
        UploadValidationFailure? failure)
    {
        Upload = upload;
        WorksheetNames = worksheetNames ?? [];
        Failure = failure;
    }

    public StoredUpload? Upload { get; }
    public IReadOnlyList<string> WorksheetNames { get; }
    public UploadValidationFailure? Failure { get; }
    public bool IsValid => Upload is not null;

    public static XlsxWorksheetStageResult Accepted(StoredUpload upload, IReadOnlyList<string> worksheetNames) =>
        new(upload, worksheetNames, null);

    public static XlsxWorksheetStageResult Rejected(UploadValidationFailure failure) =>
        new(null, null, failure);
}
