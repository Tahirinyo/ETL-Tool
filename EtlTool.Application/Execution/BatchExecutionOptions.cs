namespace EtlTool.Application.Execution;

public sealed class BatchExecutionOptions
{
    public const string SectionName = "EtlExecution";

    public int BatchSize { get; set; } = 1000;

    public void Validate()
    {
        if (BatchSize <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{SectionName}:BatchSize' must be greater than zero.");
        }
    }
}
