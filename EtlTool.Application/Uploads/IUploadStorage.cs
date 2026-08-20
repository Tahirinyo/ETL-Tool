namespace EtlTool.Application.Uploads;

public interface IUploadStorage
{
    /// <summary>
    /// Stores an upload and owns it for this application instance until <see cref="DeleteAsync"/> is called.
    /// </summary>
    Task<StoredUpload> StoreAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends this application instance's ownership and deletes the stored upload. Ownership remains
    /// released when physical deletion fails so age-based orphan cleanup can retry it.
    /// </summary>
    Task DeleteAsync(
        StoredUpload upload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes expired generated uploads that are not owned by this application instance.
    /// </summary>
    Task DeleteExpiredAsync(
        DateTimeOffset expiresBefore,
        CancellationToken cancellationToken);
}
