namespace EtlTool.Application.Execution;

public sealed class RunAdmissionOptions
{
    public const string SectionName = "RunAdmission";
    public const int MaximumQueueAdmissionTimeoutMilliseconds = 60_000;

    public int QueueAdmissionTimeoutMilliseconds { get; set; } = 5_000;

    public TimeSpan QueueAdmissionTimeout =>
        TimeSpan.FromMilliseconds(QueueAdmissionTimeoutMilliseconds);

    public void Validate()
    {
        if (QueueAdmissionTimeoutMilliseconds is < 1 or > MaximumQueueAdmissionTimeoutMilliseconds)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:QueueAdmissionTimeoutMilliseconds' must be between 1 and {MaximumQueueAdmissionTimeoutMilliseconds}.");
        }
    }
}
