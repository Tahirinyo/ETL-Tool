namespace EtlTool.Application.Uploads;

public sealed class UploadValidationResult
{
    private UploadValidationResult(
        StoredUpload? upload,
        UploadValidationFailure? failure)
    {
        Upload = upload;
        Failure = failure;
    }

    public bool IsValid => Upload is not null;

    public StoredUpload? Upload { get; }

    public UploadValidationFailure? Failure { get; }

    public static UploadValidationResult Accepted(StoredUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        return new UploadValidationResult(upload, failure: null);
    }

    public static UploadValidationResult Rejected(UploadValidationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new UploadValidationResult(upload: null, failure);
    }
}
