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

    /// <summary>备份存储目录（自定义位置）。null/空 = 数据目录下的 backups\。</summary>
    public string? BackupRootPath { get; set; }

    /// <summary>库根文件监视（v0.7.2）：外部改动防抖后自动重扫，免手动点「重新扫描」。</summary>
    public bool AutoWatch { get; set; } = true;
}
