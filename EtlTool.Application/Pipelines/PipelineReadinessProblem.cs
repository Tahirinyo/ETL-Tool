namespace EtlTool.Application.Pipelines;

public sealed record PipelineReadinessProblem(string Component, string Message);
