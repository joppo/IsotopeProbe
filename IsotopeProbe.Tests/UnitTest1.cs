using System.Text.Json;
using IsotopeProbe.Nuclei;

namespace IsotopeProbe.Tests;

public sealed class NucleiFindingParserTests
{
    private readonly NucleiFindingParser _parser = new();

    [Fact]
    public void Parse_MapsRealisticNucleiJsonToFinding()
    {
        const string json = """
            {
              "template-id": "poc-test",
              "template-path": "/home/mule/nuclei-poc/templates/poc-test.yaml",
              "template-encoded": "ZW5jb2RlZC10ZW1wbGF0ZQ==",
              "info": {
                "name": "PoC Test",
                "author": ["me"],
                "tags": ["poc", "config"],
                "severity": "info"
              },
              "type": "http",
              "host": "example.com",
              "port": "443",
              "scheme": "https",
              "url": "https://example.com",
              "matched-at": "https://example.com",
              "request": "GET / HTTP/1.1",
              "response": "HTTP/1.1 200 OK",
              "ip": "172.66.147.243",
              "timestamp": "2026-08-31T10:48:41.513209994+03:00",
              "curl-command": "curl -X GET https://example.com",
              "matcher-status": true
            }
            """;

        var finding = _parser.Parse(json);

        Assert.Equal("poc-test", finding.TemplateId);
        Assert.Equal("PoC Test", finding.Name);
        Assert.Equal("info", finding.Severity);
        Assert.Equal("https://example.com", finding.MatchedAt);
        Assert.Equal("/home/mule/nuclei-poc/templates/poc-test.yaml", finding.TemplatePath);
        Assert.Equal(["me"], finding.Authors);
        Assert.Equal(["poc", "config"], finding.Tags);
        Assert.Equal("http", finding.Type);
        Assert.Equal("example.com", finding.Host);
        Assert.Equal("443", finding.Port);
        Assert.Equal("https", finding.Scheme);
        Assert.Equal("https://example.com", finding.Url);
        Assert.Equal("172.66.147.243", finding.IpAddress);
        Assert.Equal("2026-08-31T10:48:41.513209994+03:00", finding.Timestamp);
        Assert.True(finding.MatcherStatus);
        Assert.Equal("GET / HTTP/1.1", finding.Request);
        Assert.Equal("HTTP/1.1 200 OK", finding.Response);
        Assert.Equal("curl -X GET https://example.com", finding.CurlCommand);
    }

    [Fact]
    public void Parse_WhenRequiredPropertyIsMissing_ThrowsJsonException()
    {
        const string json = """
            {
              "template-id": "example-template",
              "info": { "severity": "low" },
              "matched-at": "https://example.com/"
            }
            """;

        var exception = Assert.Throws<JsonException>(() => _parser.Parse(json));

        Assert.Contains("info.name", exception.Message);
    }

    [Fact]
    public void Parse_WhenJsonIsMalformed_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => _parser.Parse("{not-json}"));
    }
}
