namespace EtlTool.Web.Models.Runs;

internal static class RunDisplayFormatting
{
    public static string Timestamp(DateTimeOffset? value, string unavailableText) => value is null
        ? unavailableText
        : value.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    public static string Duration(DateTimeOffset? startedAt, DateTimeOffset? completedAt)
    {
        if (startedAt is null || completedAt is null) return "In progress";

        var duration = completedAt.Value - startedAt.Value;
        return duration < TimeSpan.Zero ? "Unavailable" : duration.ToString("c");
    }
}
