namespace EtlTool.Application.Validations;

internal static class ValidationValuePresence
{
    public static bool IsPresent(object? value) =>
        value is not null
        && (value is not string text || !string.IsNullOrWhiteSpace(text));
}
