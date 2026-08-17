using System.ComponentModel.DataAnnotations;

namespace EtlTool.Web.Models.Pipelines;

public sealed class PipelineFormViewModel
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
}
