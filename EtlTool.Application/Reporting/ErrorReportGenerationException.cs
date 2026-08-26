namespace EtlTool.Application.Reporting;

public sealed class ErrorReportGenerationException : Exception
{
    public ErrorReportGenerationException(Exception innerException)
        : base("Error report generation failed.", innerException)
    {
    }
}
