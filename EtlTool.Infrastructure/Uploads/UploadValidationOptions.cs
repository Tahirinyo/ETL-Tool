namespace EtlTool.Infrastructure.Uploads;

public sealed class UploadValidationOptions
{
    public const string SectionName = "UploadValidation";
    public const int MvpMaximumDataRowCount = 100_000;

    public long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024;

    public int MaxDataRowCount { get; set; } = MvpMaximumDataRowCount;

    public void Validate()
    {
        if (MaxFileSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:MaxFileSizeBytes' must be greater than zero.");
        }

        if (MaxDataRowCount is < 1 or > MvpMaximumDataRowCount)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:MaxDataRowCount' must be between 1 and {MvpMaximumDataRowCount}.");
        }
    }
}
