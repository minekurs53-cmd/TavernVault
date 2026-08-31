using System.Text.Json;
using System.Text.Json.Serialization;

namespace TavernVault.Core.Models;

public enum LibrarySource { Normal = 0, TavernST = 1, TavernTT = 2 }

public class LibraryRoot
{
    public string Path { get; set; } = "";
    public LibrarySource Source { get; set; } = LibrarySource.Normal;
}

public class LibraryRootConverter : JsonConverter<LibraryRoot>
{
    public override LibraryRoot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new LibraryRoot { Path = reader.GetString() ?? "", Source = LibrarySource.Normal };

        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;
        var root = new LibraryRoot
        {
            Path = el.TryGetProperty("Path", out var p) ? p.GetString() ?? "" : ""
        };
        if (el.TryGetProperty("Source", out var s) && s.ValueKind == JsonValueKind.Number)
            root.Source = (LibrarySource)s.GetInt32();
        return root;
    }

    public override void Write(Utf8JsonWriter writer, LibraryRoot value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Path", value.Path);
        writer.WriteNumber("Source", (int)value.Source);
        writer.WriteEndObject();
    }
}
