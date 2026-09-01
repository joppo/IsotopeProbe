using System.Text.Json;
using IsotopeProbe.Domain;

namespace IsotopeProbe.Nuclei;

public sealed class NucleiFindingParser
{
    public Finding Parse(string jsonLine)
    {
        var dto = JsonSerializer.Deserialize<NucleiFindingDto>(jsonLine)
            ?? throw new JsonException("Nuclei returned an empty JSON value.");

        return new Finding
        {
            TemplateId = Required(dto.TemplateId, "template-id"),
            Name = Required(dto.Info?.Name, "info.name"),
            Severity = Required(dto.Info?.Severity, "info.severity"),
            MatchedAt = Required(dto.MatchedAt, "matched-at"),
            TemplatePath = dto.TemplatePath,
            Authors = dto.Info?.Authors?.AsReadOnly() ?? [],
            Tags = dto.Info?.Tags?.AsReadOnly() ?? [],
            Type = dto.Type,
            Host = dto.Host,
            Port = dto.Port,
            Scheme = dto.Scheme,
            Url = dto.Url,
            IpAddress = dto.Ip,
            Timestamp = dto.Timestamp,
            MatcherStatus = dto.MatcherStatus,
            Request = dto.Request,
            Response = dto.Response,
            CurlCommand = dto.CurlCommand
        };
    }

    private static string Required(string? value, string propertyName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new JsonException($"Nuclei finding is missing required property '{propertyName}'.")
            : value;
}
