using System.Collections.ObjectModel;

namespace EtlTool.Application.Pipelines;

public sealed class PipelineReadinessResult
{
    public PipelineReadinessResult(IEnumerable<PipelineReadinessProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        Problems = new ReadOnlyCollection<PipelineReadinessProblem>(problems.ToArray());
    }

    public bool IsReady => Problems.Count == 0;

    public IReadOnlyList<PipelineReadinessProblem> Problems { get; }
}
