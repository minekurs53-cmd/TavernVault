using System.Text.Json.Nodes;
using TavernVault.Core.Cards;
using TavernVault.Core.Models;

namespace TavernVault.Core.Detection;

/// <summary>基于文件内容（而非所在文件夹）识别资源类型。</summary>
public static class TypeDetector
{
    // ST 主题文件的特征字段（出现多个即判定为美化主题）。
    // v0.6.0 对齐官方 power-user.js 的 themeProperties：
    //   italics_color→italics_text_color、quote_color→quote_text_color（原键名官方不存在），
    //   补 blur_tint_color；bogus_folders 经官方源码核实存在（power_user.bogus_folders），保留。
    private static readonly string[] ThemeKeys =
    [
        "custom_css", "blur_strength", "main_text_color", "italics_text_color",
        "quote_text_color", "blur_tint_color", "shadow_color", "avatar_style", "chat_display",
        "bogus_folders", "fast_ui_mode", "movingUI", "theme_color",
    ];

    // 文本补全预设（官方 TextGen Settings/ 目录，如 Universal-Light.json / Deterministic.json）。
    // 核心采样字段（default/content/presets/textgen/*.json 实测均含 temp 与 rep_pen）。
    private static readonly string[] TextGenCoreKeys = ["temp", "rep_pen"];

    // 其余官方采样字段（注意官方名：typical_p 而非 typical；mirostat_tau/eta 而非 mirostat_lr）。
    private static readonly string[] TextGenSamplerKeys =
    [
        "top_p", "top_k", "top_a", "min_p", "typical_p", "tfs", "rep_pen_range",
        "rep_pen_slope", "rep_pen_decay", "mirostat_mode", "mirostat_tau", "mirostat_eta",
        "add_bos_token", "ban_eos_token", "skip_special_tokens", "temperature_last",
        "smoothing_factor", "smoothing_curve", "dry_multiplier", "dynatemp",
        "sampler_priority", "xtc_probability", "nsigma", "min_temp", "max_temp",
    ];

    // 指令模板（官方 instruct/ 目录，如 ChatML.json；separator_sequence 为旧版遗留字段，
    // 官方 migrateInstructModeSettings 会把它并入 output_suffix，旧文件仍可能携带）。
    private static readonly string[] InstructKeys =
    [
        "input_sequence", "output_sequence", "system_sequence", "stop_sequence",
        "separator_sequence", "first_output_sequence", "last_output_sequence",
        "first_input_sequence", "last_input_sequence", "last_system_sequence",
    ];

    // 上下文模板（官方 context/ 目录）。story_string 最具区分性，可单独命中；
    // 其余为旧版模板 / 任务约定的次级特征（官方新旧模板均含 example_separator、chat_start）。
    private static readonly string[] ContextKeys =
    [
        "char", "names", "example_separator", "chat_start", "chat_end", "trim_examples",
    ];

    public static ItemKind DetectJson(JsonObject obj)
    {
        // V2/V3 角色卡
        if (obj["spec"] is JsonValue spec && spec.TryGetValue<string>(out var s) &&
            s.StartsWith("chara_card", StringComparison.OrdinalIgnoreCase))
            return ItemKind.Character;
        if (obj["data"] is JsonObject d && d["name"] is not null && d["description"] is not null)
            return ItemKind.Character;

        // 世界书：entries（对象或数组，元素含 key/content）
        if (obj["entries"] is JsonNode entries && HasEntryShape(entries))
            return ItemKind.Lorebook;

        // 对话预设：prompts 数组 + 采样器字段
        if (obj["prompts"] is JsonArray)
            return ItemKind.Preset;

        // V1 平铺角色卡
        if (obj["name"] is not null && obj["description"] is not null &&
            (obj["first_mes"] is not null || obj["personality"] is not null || obj["scenario"] is not null))
            return ItemKind.Character;

        // 系统提示模板（官方 sysprompt/ 目录，如 Blank.json：{name, content, post_history}）。
        // post_history 是与酒馆助手脚本 {name, content} 区分的精确特征 → 必须置于脚本判定之前。
        // （官方文件仅这三个键；裸 {name, content} 与脚本无法区分，按脚本处理——见 v0.6.0 handoff）
        if (obj["name"] is not null && obj["content"] is not null && obj.ContainsKey("post_history"))
            return ItemKind.SysPrompt;

        // 酒馆助手脚本 / ST 正则
        if (obj["name"] is not null && obj["content"] is not null)
            return ItemKind.Script;
        if (obj["scriptName"] is not null && obj["findRegex"] is not null)
            return ItemKind.Script;

        // 快捷回复（官方 QuickReplies/ 目录）：v2 结构含 qrList 数组
        // （quick-reply/src/QuickReplySet.js：{version:2, name, qrList:[…], disableSend, …}）；
        // quickReplies 数组为旧命名兼容（内容判定不依赖其元素结构）。
        if (obj["qrList"] is JsonArray || obj["quickReplies"] is JsonArray)
            return ItemKind.QuickReplies;

        // 指令模板：≥2 个序列特征（单一字段不足以区分普通字符串配置）
        if (InstructKeys.Count(obj.ContainsKey) >= 2)
            return ItemKind.InstructTemplate;

        // 上下文模板：story_string 单独命中（官方模板必有）；否则按次级特征 ≥2
        if (obj.ContainsKey("story_string") || ContextKeys.Count(obj.ContainsKey) >= 2)
            return ItemKind.ContextTemplate;

        // 文本补全预设：核心采样字段同在，或命中 ≥3 个采样字段。
        // 置于主题判定之前：精确采样特征优先于主题的"两键即中"宽松规则。
        if (TextGenCoreKeys.All(obj.ContainsKey) || TextGenSamplerKeys.Count(obj.ContainsKey) >= 3)
            return ItemKind.TextGenPreset;

        // 美化主题
        int themeHits = ThemeKeys.Count(k => obj.ContainsKey(k));
        if (themeHits >= 2 || (themeHits >= 1 && obj.ContainsKey("custom_css")))
            return ItemKind.Theme;

        return ItemKind.Text;
    }

    private static bool HasEntryShape(JsonNode entries)
    {
        JsonNode? first = entries switch
        {
            JsonArray arr => arr.Count > 0 ? arr[0] : null,
            JsonObject map => map.Count > 0 ? map.First().Value : null,
            _ => null,
        };
        if (first is not JsonObject o) return entries is JsonObject map && map.Count == 0;
        return o.ContainsKey("key") || o.ContainsKey("content") || o.ContainsKey("comment");
    }

    /// <summary>非 JSON 文件按扩展名归类；PNG 会再检查是否内嵌角色卡。</summary>
    public static ItemKind DetectByExtension(string path, out bool checkEmbeddedCard)
    {
        checkEmbeddedCard = false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".png":
                checkEmbeddedCard = true;
                return ItemKind.Other; // 由调用方复核内嵌数据后改为 Character
            case ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp":
                return ItemKind.Other;
            case ".js" or ".mjs" or ".ts" or ".py" or ".qs" or ".ps1" or ".bat":
                return ItemKind.Script;
            case ".json":
                return ItemKind.Text; // 调用方会先解析内容
            case ".yaml" or ".yml" or ".md" or ".txt" or ".log" or ".css" or ".html":
                return ItemKind.Text;
            case ".zip" or ".7z" or ".rar" or ".apk" or ".tar" or ".gz":
                return ItemKind.Archive;
            default:
                return ItemKind.Other;
        }
    }
}
