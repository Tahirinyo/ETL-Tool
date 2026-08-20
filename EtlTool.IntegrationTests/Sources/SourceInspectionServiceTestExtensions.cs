using EtlTool.Application.Sources;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Sources;

namespace EtlTool.IntegrationTests.Sources;

internal static class SourceInspectionServiceTestExtensions
{
    private static readonly Guid TestPipelineId = Guid.Parse("5413fe88-2f71-4f4d-a64f-1f4e5086ad71");

    public static Task<SourceInspectionResult> StageXlsxAsync(
        this SourceInspectionService service,
        Stream content,
        string fileName,
        CancellationToken cancellationToken) =>
        service.StageXlsxAsync(TestPipelineId, content, fileName, cancellationToken);

    public static Task<SourceInspectionResult> InspectStagedXlsxAsync(
        this SourceInspectionService service,
        Guid stageId,
        string worksheetName,
        CancellationToken cancellationToken,
        SourceOptions? sourceOptions = null) =>
        service.InspectStagedXlsxAsync(
            TestPipelineId,
            stageId,
            worksheetName,
            cancellationToken,
            sourceOptions);
}
