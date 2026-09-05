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

        // 酒馆助手脚本 / ST 正则
        // （v0.6.1：官方 sysprompt 文件 {name, content, post_history} 同样落入此规则——
        //   与裸 {name, content} 一样无法与脚本可靠区分，统一按脚本归类）
        if (obj["name"] is not null && obj["content"] is not null)
            return ItemKind.Script;
        if (obj["scriptName"] is not null && obj["findRegex"] is not null)
            return ItemKind.Script;

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
