namespace IsotopeProbe.Queue;

public sealed class WebScanOptions
{
    public List<AllowedScanTarget> Targets { get; set; } = [];
    public int PerUserLimit { get; set; } = 1;
    public int ConcurrentScans { get; set; } = 1;
    public int QueueLimit { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 600;
    public int PollSeconds { get; set; } = 2;
    public string ExecutablePath { get; set; } = "nuclei";

    public void Validate()
    {
        if (PerUserLimit < 1 || QueueLimit < 1 || ConcurrentScans is < 1 or > 20 || TimeoutSeconds is < 1 or > 86400 || PollSeconds is < 1 or > 60 ||
            string.IsNullOrWhiteSpace(ExecutablePath))
            throw new ArgumentException("Invalid WebScans profile, limits, executable, timeout or poll interval.");
        if (Targets.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Targets.Count ||
            Targets.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name) ||
                !Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("WebScans targets require unique IDs, names and HTTP(S) URLs without credentials.");
    }


}

public sealed class AllowedScanTarget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}
