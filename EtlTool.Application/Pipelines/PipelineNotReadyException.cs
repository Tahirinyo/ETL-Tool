using System.Collections.ObjectModel;

namespace EtlTool.Application.Pipelines;

public sealed class PipelineNotReadyException : InvalidOperationException
{
    public PipelineNotReadyException(IReadOnlyList<PipelineReadinessProblem> problems)
        : this(problems, "The pipeline is not ready for execution.")
    {
    }

    internal PipelineNotReadyException(
        IReadOnlyList<PipelineReadinessProblem> problems,
        string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (problems.Count == 0)
        {
            throw new ArgumentException(
                "A pipeline-not-ready exception requires at least one readiness problem.",
                nameof(problems));
        }

        Problems = new ReadOnlyCollection<PipelineReadinessProblem>(problems.ToArray());
    }

    public IReadOnlyList<PipelineReadinessProblem> Problems { get; }
}
