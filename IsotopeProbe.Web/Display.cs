using System.Globalization;

namespace IsotopeProbe.Web;

public static class Display
{
    public static string Utc(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Unavailable";
    public static string Tone(string value) => value.ToLowerInvariant() switch
    {
        "critical" or "high" or "failed" => "danger",
        "medium" or "running" => "warning",
        "low" or "succeeded" => "success",
        _ => "neutral"
    };
}
