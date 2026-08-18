namespace EtlTool.Application.Uploads;

public sealed record StoredUpload(
    string OriginalFileName,
    string StoredFileName,
    string StoredFilePath,
    long SizeInBytes);
