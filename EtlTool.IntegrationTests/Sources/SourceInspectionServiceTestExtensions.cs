using EtlTool.Application.Sources;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.Infrastructure.Sources;

internal static class SourceInspectionServiceTestExtensions
{
    private static readonly Guid TestPipelineId =
        Guid.Parse("5413fe88-2f71-4f4d-a64f-1f4e5086ad71");

    public static async Task<SourceInspectionResult> InspectCsvAsync(
        this SourceInspectionService service,
        Stream content,
        string fileName,
        SourceOptions options,
        CancellationToken cancellationToken)
    {
        var result = await service.InspectCsvAsync(
            TestPipelineId,
            content,
            fileName,
            options,
            cancellationToken);

        if (result.SourceReferenceId is Guid sourceReferenceId)
        {
            await service.DiscardAsync(sourceReferenceId, CancellationToken.None);
        }

        return result;
    }

    public static Task<SourceInspectionResult> StageXlsxAsync(
        this SourceInspectionService service,
        Stream content,
        string fileName,
        CancellationToken cancellationToken) =>
        service.StageXlsxAsync(TestPipelineId, content, fileName, cancellationToken);

    public static async Task<SourceInspectionResult> InspectStagedXlsxAsync(
        this SourceInspectionService service,
        Guid stageId,
        string worksheetName,
        CancellationToken cancellationToken,
        SourceOptions? sourceOptions = null)
    {
        var result = await service.InspectStagedXlsxAsync(
            TestPipelineId,
            stageId,
            worksheetName,
            cancellationToken,
            sourceOptions);

        if (result.SourceReferenceId is Guid sourceReferenceId)
        {
            await service.DiscardAsync(sourceReferenceId, CancellationToken.None);
        }

        return result;
    }
}
