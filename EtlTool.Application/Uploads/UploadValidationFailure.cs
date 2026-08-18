namespace EtlTool.Application.Uploads;

public sealed record UploadValidationFailure(
    UploadValidationFailureCode Code,
    string Message);
