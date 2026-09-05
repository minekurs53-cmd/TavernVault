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
        Converters = { new LibraryRootConverter() },
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
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 文件存在但损坏/被锁：把坏文件改名留作证据，按默认设置继续。
            // Vault 据此（SettingsWarning + 空库根）跳过启动期自愈重扫，
            // 否则空库根会让自愈把 index.json 重写为空、丢光收藏与标签（v0.5.1）。
            try { File.Move(SettingsPath, SettingsPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}"); }
            catch (IOException) { }
            LoadSettingsWarning = "settings.json 无法读取（坏文件已保留为 .corrupt-*），本次以默认设置启动：库根列表为空，请重新登记库目录";
            return new AppSettings();
        }
    }

    /// <summary>LoadSettings 检测到设置文件损坏时的告警文本；正常为 null。</summary>
    public string? LoadSettingsWarning { get; private set; }

    public void SaveSettings(AppSettings settings)
    {
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    // 索引结构版本：条目模型变化时 +1，旧索引直接丢弃全量重建，
    // 避免增量扫描复用缺少新字段的旧条目。
    // v0.6.1：3→4——回撤 5 类官方模板分类（旧索引里的 kind 数字已失效，须重建）。
    private const int IndexVersion = 4;

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
        // 上一版索引留档：任何把索引清空/写坏的事故都可从 index.bak 找回收藏与标签
        try { if (File.Exists(IndexPath)) File.Copy(IndexPath, IndexPathBak, overwrite: true); }
        catch (IOException) { }
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

    private string IndexPathBak => Path.Combine(DataDir, "index.bak");
}
