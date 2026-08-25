namespace EtlTool.Application.Execution;

public sealed record BackgroundJob
{
    public BackgroundJob(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The background job run identifier cannot be empty.", nameof(runId));
        }

        RunId = runId;
    }

    public Guid RunId { get; }
}
