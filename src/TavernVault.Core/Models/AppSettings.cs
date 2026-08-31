namespace TavernVault.Core.Models;

/// <summary>应用设置，持久化到 %APPDATA%\TavernVault\settings.json。</summary>
public class AppSettings
{
    public List<LibraryRoot> LibraryRoots { get; set; } = [];
    public string UiTheme { get; set; } = "auto"; // auto | light | dark

    /// <summary>覆盖写入（编辑保存/还原）前自动备份原文件。</summary>
    public bool AutoBackup { get; set; } = true;

    /// <summary>每个文件保留的备份份数。</summary>
    public int MaxBackupsPerFile { get; set; } = 5;
}
