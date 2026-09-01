using System.Text.Json.Serialization;

namespace IsotopeProbe.Nuclei;

public sealed class NucleiFindingDto
{
    [JsonPropertyName("template-id")]
    public string? TemplateId { get; init; }

    [JsonPropertyName("template-path")]
    public string? TemplatePath { get; init; }

    [JsonPropertyName("template-encoded")]
    public string? TemplateEncoded { get; init; }

    [JsonPropertyName("info")]
    public NucleiInfoDto? Info { get; init; }

    [JsonPropertyName("matched-at")]
    public string? MatchedAt { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("port")]
    public string? Port { get; init; }

    [JsonPropertyName("scheme")]
    public string? Scheme { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("request")]
    public string? Request { get; init; }

    [JsonPropertyName("response")]
    public string? Response { get; init; }

    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("curl-command")]
    public string? CurlCommand { get; init; }

    [JsonPropertyName("matcher-status")]
    public bool? MatcherStatus { get; init; }
}

public sealed class NucleiInfoDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("author")]
    public List<string>? Authors { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }
}
