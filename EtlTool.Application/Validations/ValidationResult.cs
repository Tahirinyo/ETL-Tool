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
        return Invalid(row, new[] { error });
    }

    public static ValidationResult Invalid(
        DataRow row,
        IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException(
                "An invalid validation result requires at least one error.",
                nameof(errors));
        }

        var copiedErrors = errors.ToArray();
        if (copiedErrors.Any(error => error is null))
        {
            throw new ArgumentException(
                "The validation error collection contains an invalid error.",
                nameof(errors));
        }

        return new ValidationResult(row, copiedErrors);
    }
}
