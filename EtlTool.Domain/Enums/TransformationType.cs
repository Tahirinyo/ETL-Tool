namespace EtlTool.Domain.Enums;

public enum TransformationType
{
    Unspecified = 0,
    Trim = 1,
    ToUpper = 2,
    ToLower = 3,
    ConvertToString = 4,
    ConvertToInteger = 5,
    ConvertToDecimal = 6,
    ConvertToDate = 7,
    SetDefaultValue = 8,
    FilterRow = 9,
    FindAndReplace = 10,
    Deduplicate = 11
}
