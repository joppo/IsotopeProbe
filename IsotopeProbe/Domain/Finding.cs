namespace IsotopeProbe.Domain;

public sealed class Finding
{
    public int Id { get; set; }
    public int ScanExecutionId { get; set; }
    public ScanExecution ScanExecution { get; set; } = null!;

    public required string RawJson { get; set; }

    public required string TemplateId { get; set; }

    public required string Name { get; set; }

    public required string Severity { get; set; }

    public required string MatchedAt { get; set; }

    public string? TemplatePath { get; set; }

    public List<string> Authors { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public string? Type { get; set; }

    public string? Host { get; set; }

    public string? Port { get; set; }

    public string? Scheme { get; set; }

    public string? Url { get; set; }

    public string? IpAddress { get; set; }

    public string? Timestamp { get; set; }

    public bool? MatcherStatus { get; set; }

    public string? Request { get; set; }

    public string? Response { get; set; }

    public string? CurlCommand { get; set; }
}
