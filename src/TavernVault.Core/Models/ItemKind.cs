namespace TavernVault.Core.Models;

/// <summary>资源大类。识别依据是文件内容而非所在文件夹。</summary>
/// <remarks>
/// v0.6.1 回撤了 v0.6.0 曾追加的 5 类（textgen/instruct/context/sysprompt/quickreplies）：
/// 这些分类缺乏编辑价值且个别规则会误收预设文件。Kind 以数字（kindValue）持久化到
/// index.json，本次识别规则变更已随索引版本 3→4 触发全量重建，不存在错位问题。
/// 此后若再新增枚举值，仍须一律追加在末尾。
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
}

public static class ItemKindText
{
    // 顺序即侧栏类型分区的展示顺序（与枚举数值无关）
    public static readonly (ItemKind Kind, string Key, string Label)[] All =
    [
        (ItemKind.Character,      "character",    "角色卡"),
        (ItemKind.Lorebook,       "lorebook",     "世界书"),
        (ItemKind.Preset,         "preset",       "预设"),
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
