namespace TavernVault.Core.Models;

/// <summary>资源大类。识别依据是文件内容而非所在文件夹。</summary>
/// <remarks>
/// v0.6.0 新增的 5 类一律追加在枚举末尾：Kind 以数字（kindValue）持久化到 index.json，
/// 中间插值会使旧索引错位（索引版本未变时不会重建）。
/// </remarks>
public enum ItemKind
{
    Character,   // 角色卡（PNG 内嵌数据或 V1/V2/V3 JSON）
    Lorebook,    // 世界书
    Preset,      // 对话预设（OpenAI Settings）
    Theme,       // 酒馆界面主题 / 美化
    Script,      // 酒馆助手脚本、正则等
    Text,        // 其他可读文本（yaml/md/txt/ts…）
    Archive,     // 压缩包
    Other,       // 其它二进制
    TextGenPreset,     // 文本补全预设（TextGen Settings/，官方目录名见 TavernDetector.Subdirs）
    InstructTemplate,  // 指令模板（instruct/）
    ContextTemplate,   // 上下文模板（context/）
    SysPrompt,         // 系统提示模板（sysprompt/）
    QuickReplies,      // 快捷回复（QuickReplies/）
}

public static class ItemKindText
{
    // 顺序即侧栏类型分区的展示顺序（与枚举数值无关）
    public static readonly (ItemKind Kind, string Key, string Label)[] All =
    [
        (ItemKind.Character,      "character",    "角色卡"),
        (ItemKind.Lorebook,       "lorebook",     "世界书"),
        (ItemKind.Preset,         "preset",       "预设"),
        (ItemKind.TextGenPreset,  "textgen",      "文本补全预设"),
        (ItemKind.InstructTemplate, "instruct",   "指令模板"),
        (ItemKind.ContextTemplate,  "context",    "上下文模板"),
        (ItemKind.SysPrompt,      "sysprompt",    "系统提示模板"),
        (ItemKind.QuickReplies,   "quickreplies", "快捷回复"),
        (ItemKind.Theme,          "theme",        "美化"),
        (ItemKind.Script,         "script",       "脚本"),
        (ItemKind.Text,           "text",         "文本"),
        (ItemKind.Archive,        "archive",      "压缩包"),
        (ItemKind.Other,          "other",        "其他"),
    ];

    public static string KeyOf(ItemKind kind) =>
        All.FirstOrDefault(a => a.Kind == kind).Key is { Length: > 0 } k ? k : "other";

    public static string LabelOf(ItemKind kind) =>
        All.FirstOrDefault(a => a.Kind == kind).Label is { Length: > 0 } l ? l : "其他";
}
