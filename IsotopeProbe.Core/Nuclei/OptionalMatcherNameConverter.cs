using System.Text.Json;
using System.Text.Json.Serialization;

namespace IsotopeProbe.Nuclei;

// Applied only to matcher-name; all other JSON fields retain their existing rules.
public sealed class OptionalMatcherNameConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        using var ignored = JsonDocument.ParseValue(ref reader);
        return null;
    }
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
