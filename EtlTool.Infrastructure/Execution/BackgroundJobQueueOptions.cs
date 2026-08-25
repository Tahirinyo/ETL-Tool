namespace EtlTool.Infrastructure.Execution;

public sealed class BackgroundJobQueueOptions
{
    public const string SectionName = "BackgroundJobQueue";

    public int Capacity { get; set; } = 100;

    public void Validate()
    {
        if (Capacity <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:Capacity' must be greater than zero.");
        }
    }
}
