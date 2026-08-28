namespace TavernVault.Core.Models;

/// <summary>资源大类。识别依据是文件内容而非所在文件夹。</summary>
public enum ItemKind
{
    Character,   // 角色卡（PNG 内嵌数据或 V1/V2/V3 JSON）
    Lorebook,    // 世界书
    Preset,      // 对话预设
    Theme,       // 酒馆界面主题 / 美化
    Script,      // 酒馆助手脚本、正则等
    Text,        // 其他可读文本（yaml/md/txt/ts…）
    Archive,     // 压缩包
    Other,       // 其它二进制
}

public static class ItemKindText
{
    public static readonly (ItemKind Kind, string Key, string Label)[] All =
    [
        (ItemKind.Character, "character", "角色卡"),
        (ItemKind.Lorebook,  "lorebook",  "世界书"),
        (ItemKind.Preset,    "preset",    "预设"),
        (ItemKind.Theme,     "theme",     "美化"),
        (ItemKind.Script,    "script",    "脚本"),
        (ItemKind.Text,      "text",      "文本"),
        (ItemKind.Archive,   "archive",   "压缩包"),
        (ItemKind.Other,     "other",     "其他"),
    ];

    public static string KeyOf(ItemKind kind) =>
        All.FirstOrDefault(a => a.Kind == kind).Key is { Length: > 0 } k ? k : "other";

    public static string LabelOf(ItemKind kind) =>
        All.FirstOrDefault(a => a.Kind == kind).Label is { Length: > 0 } l ? l : "其他";
}
