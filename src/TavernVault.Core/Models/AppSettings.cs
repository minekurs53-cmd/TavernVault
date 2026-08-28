namespace TavernVault.Core.Models;

/// <summary>应用设置，持久化到 %APPDATA%\TavernVault\settings.json。</summary>
public class AppSettings
{
    public List<string> LibraryRoots { get; set; } = [];
    public string UiTheme { get; set; } = "auto"; // auto | light | dark
}
