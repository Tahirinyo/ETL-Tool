using EtlTool.Application.Extraction;

namespace EtlTool.Application.Validations;

public sealed class ValidationResult
{
    private ValidationResult(DataRow row, IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(errors);

        Row = row;
        Errors = errors;
    }

    public DataRow Row { get; }

    public IReadOnlyList<ValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Valid(DataRow row) => new(row, []);

    public static ValidationResult Invalid(DataRow row, ValidationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ValidationResult(row, [error]);
    }
}
