using EtlTool.Application.Pipelines;

namespace EtlTool.Application.Execution;

public interface IRunAdmissionService
{
    Task<RunAdmissionResult> AdmitAsync(
        Guid pipelineId,
        CancellationToken cancellationToken);
}

public enum RunAdmissionStatus
{
    Admitted,
    PipelineNotFound,
    PipelineNotReady,
    SourceUnavailable,
    RunAlreadyActive,
    Failed
}

public sealed class RunAdmissionResult
{
    private RunAdmissionResult(
        RunAdmissionStatus status,
        Guid? runId = null,
        Guid? pipelineId = null,
        string? pipelineName = null,
        IReadOnlyList<PipelineReadinessProblem>? readinessProblems = null,
        Exception? failure = null)
    {
        Status = status;
        RunId = runId;
        PipelineId = pipelineId;
        PipelineName = pipelineName ?? string.Empty;
        ReadinessProblems = readinessProblems ?? [];
        Failure = failure;
    }

    public RunAdmissionStatus Status { get; }

    public Guid? RunId { get; }

    public Guid? PipelineId { get; }

    public string PipelineName { get; }

    public IReadOnlyList<PipelineReadinessProblem> ReadinessProblems { get; }

    public Exception? Failure { get; }

    internal static RunAdmissionResult Admitted(Guid pipelineId, string pipelineName, Guid runId) =>
        new(RunAdmissionStatus.Admitted, runId, pipelineId, pipelineName);

    internal static RunAdmissionResult PipelineNotFound() =>
        new(RunAdmissionStatus.PipelineNotFound);

    internal static RunAdmissionResult PipelineNotReady(
        Guid pipelineId,
        string pipelineName,
        IReadOnlyList<PipelineReadinessProblem> problems) =>
        new(RunAdmissionStatus.PipelineNotReady,
            pipelineId: pipelineId,
            pipelineName: pipelineName,
            readinessProblems: problems);

    internal static RunAdmissionResult SourceUnavailable(Guid pipelineId, string pipelineName) =>
        new(RunAdmissionStatus.SourceUnavailable,
            pipelineId: pipelineId,
            pipelineName: pipelineName);

    internal static RunAdmissionResult RunAlreadyActive(Guid pipelineId, string pipelineName) =>
        new(RunAdmissionStatus.RunAlreadyActive,
            pipelineId: pipelineId,
            pipelineName: pipelineName);

    internal static RunAdmissionResult Failed(
        Guid pipelineId,
        string pipelineName,
        Exception failure) =>
        new(RunAdmissionStatus.Failed,
            pipelineId: pipelineId,
            pipelineName: pipelineName,
            failure: failure);
}
