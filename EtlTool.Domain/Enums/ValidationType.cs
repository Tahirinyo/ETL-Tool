namespace EtlTool.Domain.Enums;

public enum ValidationType
{
    Unspecified = 0,
    Required = 1,
    EmailFormat = 2,
    NumericRange = 3,
    TextLengthRange = 4,
    DateRange = 5,
    UpsertKeyRequired = 6
}
