namespace EtlTool.Application.Uploads;

public interface IUploadStorage
{
    Task<StoredUpload> StoreAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken);
}
