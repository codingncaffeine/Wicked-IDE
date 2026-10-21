using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wicked.Core.Settings;

/// <summary>editor.rulers as VS Code takes it: an array of column numbers, or of { "column": n, "color": "..." } objects.</summary>
public sealed class RulerColumnsConverter : JsonConverter<int[]>
{
    public override int[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("editor.rulers is a list of columns, like [80, 120]");
        var columns = new List<int>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number) columns.Add(reader.GetInt32());
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var item = JsonDocument.ParseValue(ref reader);
                if (item.RootElement.TryGetProperty("column", out var column) && column.TryGetInt32(out var n)) columns.Add(n);
            }
            else throw new JsonException("editor.rulers is a list of columns, like [80, 120]");
        }

        return [.. columns.Where(c => c > 0).Distinct().Order()];
    }

    public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var column in value) writer.WriteNumberValue(column);
        writer.WriteEndArray();
    }
}
