namespace EtlTool.Application.MongoDB;

public sealed class MongoTargetValidationResult
{
    private MongoTargetValidationResult(bool isAllowed, string? failureMessage)
    {
        IsAllowed = isAllowed;
        FailureMessage = failureMessage;
    }

    public bool IsAllowed { get; }

    public string? FailureMessage { get; }

    public static MongoTargetValidationResult Allowed { get; } = new(true, null);

    public static MongoTargetValidationResult Rejected(string failureMessage)
    {
        if (string.IsNullOrWhiteSpace(failureMessage))
        {
            throw new ArgumentException("A target rejection message is required.", nameof(failureMessage));
        }

        return new MongoTargetValidationResult(false, failureMessage);
    }
}
