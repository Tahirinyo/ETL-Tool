namespace EtlTool.Application.Uploads;

public enum UploadValidationFailureCode
{
    InvalidFileName = 1,
    UnsupportedExtension = 2,
    UnsupportedSourceType = 3,
    SourceTypeMismatch = 4,
    WorksheetRequired = 5,
    EmptyFile = 6,
    FileTooLarge = 7,
    RowLimitExceeded = 8,
    InvalidSourceFile = 9
}
