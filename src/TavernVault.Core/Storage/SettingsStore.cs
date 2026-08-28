using System.Text.Json;
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

    public List<LibraryItem> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<List<LibraryItem>>(File.ReadAllText(IndexPath), JsonOpts) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return [];
    }

    public void SaveIndex(IEnumerable<LibraryItem> items)
    {
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOpts));
        File.Move(tmp, IndexPath, overwrite: true);
    }
}
