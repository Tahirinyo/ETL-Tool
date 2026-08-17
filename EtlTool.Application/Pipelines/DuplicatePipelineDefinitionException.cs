namespace EtlTool.Application.Pipelines;

public sealed class DuplicatePipelineDefinitionException : Exception
{
    public DuplicatePipelineDefinitionException(Guid pipelineId, Exception? innerException = null)
        : base($"A pipeline definition with identifier '{pipelineId}' already exists.", innerException)
    {
        PipelineId = pipelineId;
    }

    public Guid PipelineId { get; }
}
