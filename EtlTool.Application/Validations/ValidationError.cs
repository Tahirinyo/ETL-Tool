namespace EtlTool.Application.Validations;

public sealed record ValidationError(string Field, string Message);
