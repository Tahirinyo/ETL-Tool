using EtlTool.Application.Uploads;
using EtlTool.Infrastructure.Uploads;

namespace EtlTool.UnitTests.Application.Uploads;

public sealed class UploadValidationContractTests
{
    [Fact]
    public void Accepted_ContainsOnlyStoredUpload()
    {
        var upload = new StoredUpload(
            "source.csv",
            $"{Guid.NewGuid():N}.upload",
            Path.GetFullPath("stored.upload"),
            42);

        var result = UploadValidationResult.Accepted(upload);

        Assert.True(result.IsValid);
        Assert.Same(upload, result.Upload);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Rejected_ContainsOnlyFailure()
    {
        var failure = new UploadValidationFailure(
            UploadValidationFailureCode.EmptyFile,
            "The source file cannot be empty.");

        var result = UploadValidationResult.Rejected(failure);

        Assert.False(result.IsValid);
        Assert.Null(result.Upload);
        Assert.Same(failure, result.Failure);
    }

    [Fact]
    public void ResultFactories_RejectNullValues()
    {
        Assert.Throws<ArgumentNullException>(
            () => UploadValidationResult.Accepted(null!));
        Assert.Throws<ArgumentNullException>(
            () => UploadValidationResult.Rejected(null!));
    }

    [Fact]
    public void UploadValidationOptions_DefaultsMatchMvpLimits()
    {
        var options = new UploadValidationOptions();

        options.Validate();

        Assert.Equal(100L * 1024 * 1024, options.MaxFileSizeBytes);
        Assert.Equal(100_000, options.MaxDataRowCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 100_001)]
    public void UploadValidationOptions_RejectInvalidLimits(
        long maxFileSizeBytes,
        int maxDataRowCount)
    {
        var options = new UploadValidationOptions
        {
            MaxFileSizeBytes = maxFileSizeBytes,
            MaxDataRowCount = maxDataRowCount
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
