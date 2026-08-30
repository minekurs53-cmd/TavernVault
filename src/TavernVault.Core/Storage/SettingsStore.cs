using System.Text.Json;
using System.Text.Json.Nodes;
using TavernVault.Core.Models;

namespace TavernVault.Core.Storage;

/// <summary>设置与索引的持久化。数据目录：%APPDATA%\TavernVault。</summary>
public sealed class SettingsStore
{
    public string DataDir { get; }
    private string SettingsPath => Path.Combine(DataDir, "settings.json");
    private string IndexPath => Path.Combine(DataDir, "index.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsStore(string? dataDir = null)
    {
        DataDir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TavernVault");
        Directory.CreateDirectory(DataDir);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    // 索引结构版本：条目模型变化时 +1，旧索引直接丢弃全量重建，
    // 避免增量扫描复用缺少新字段的旧条目。
    private const int IndexVersion = 2;

    public List<LibraryItem> LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return [];
            if (JsonNode.Parse(File.ReadAllText(IndexPath)) is not JsonObject node) return [];
            if (node["version"]?.GetValue<int>() != IndexVersion) return [];
            if (node["items"] is JsonNode itemsNode &&
                JsonSerializer.Deserialize<LibraryItem[]>(itemsNode.ToJsonString(), JsonOpts) is { } items)
                return [.. items];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException
                                       or FormatException)
        { }
        return [];
    }

    public void SaveIndex(IEnumerable<LibraryItem> items)
    {
        var tmp = IndexPath + ".tmp";
        var payload = new JsonObject
        {
            ["version"] = IndexVersion,
            ["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["items"] = JsonSerializer.SerializeToNode(items, JsonOpts),
        };
        File.WriteAllText(tmp, payload.ToJsonString(JsonOpts));
        File.Move(tmp, IndexPath, overwrite: true);
    }
}
