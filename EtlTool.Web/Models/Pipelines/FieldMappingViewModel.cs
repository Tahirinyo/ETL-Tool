using System.ComponentModel.DataAnnotations;
using EtlTool.Domain.Enums;

namespace EtlTool.Web.Models.Pipelines;

public sealed class FieldMappingViewModel
{
    public List<FieldMappingFieldViewModel> Fields { get; set; } = [];

    public bool IsSaved { get; set; }
}

public sealed class FieldMappingFieldViewModel
{
    [Required]
    public string SourceField { get; set; } = string.Empty;

    public SourceFieldType DataType { get; set; }

    public bool IsIncluded { get; set; } = true;

    public string? TargetField { get; set; }
}
