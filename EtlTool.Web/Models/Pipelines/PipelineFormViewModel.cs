using System.ComponentModel.DataAnnotations;

namespace EtlTool.Web.Models.Pipelines;

public sealed class PipelineFormViewModel
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Display(Name = "Destination database")]
    public string? DestinationDatabase { get; set; }

    [Display(Name = "Destination collection")]
    public string? DestinationCollection { get; set; }

    [Display(Name = "Upsert key")]
    public string? UpsertKeyField { get; set; }

    public IReadOnlyList<string> AvailableMappedFields { get; set; } = [];
}
