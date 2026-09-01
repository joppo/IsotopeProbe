namespace IsotopeProbe.Domain;

public sealed record Finding
{
    public required string TemplateId { get; init; }

    public required string Name { get; init; }

    public required string Severity { get; init; }

    public required string MatchedAt { get; init; }

    public string? TemplatePath { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Type { get; init; }

    public string? Host { get; init; }

    public string? Port { get; init; }

    public string? Scheme { get; init; }

    public string? Url { get; init; }

    public string? IpAddress { get; init; }

    public string? Timestamp { get; init; }

    public bool? MatcherStatus { get; init; }

    public string? Request { get; init; }

    public string? Response { get; init; }

    public string? CurlCommand { get; init; }
}
