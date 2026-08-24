using System.Collections.ObjectModel;
using EtlTool.Application.Pipelines;

namespace EtlTool.Application.Preview;

public sealed class PipelineNotReadyException : InvalidOperationException
{
    public PipelineNotReadyException(IReadOnlyList<PipelineReadinessProblem> problems)
        : base("The pipeline is not ready for preview.")
    {
        ArgumentNullException.ThrowIfNull(problems);

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
