using System.Text.Json.Nodes;
using TavernVault.Core.Models;

namespace TavernVault.Core;

/// <summary>
/// 「新建文件」空白模板（v0.6.0；v0.6.1 随 5 类官方模板分类回撤收敛为 6 类）。
/// 模板结构对齐 SillyTavern 官方用户数据格式，且必须能被自家 TypeDetector.DetectJson
/// 识别回对应 kind（模板→识别回路由 TemplatesTests 单测硬验收）。
/// </summary>
public static class ContentTemplates
{
    /// <summary>
    /// 可新建类型的扩展名（带点，直接拼接文件名）。
    /// archive/other 是二进制容器，无法给出有意义的空白模板 → null（不支持新建）。
    /// </summary>
    public static string? ExtensionFor(ItemKind kind) => kind switch
    {
        ItemKind.Character or ItemKind.Lorebook or ItemKind.Preset
            or ItemKind.Theme or ItemKind.Script => ".json",
        ItemKind.Text => ".txt",
        _ => null,
    };

    /// <summary>
    /// 按类型生成 JSON 模板根对象（name 注入官方格式的语义字段）。
    /// text 是纯文本走 <see cref="CreateText"/>，这里返回 null。
    /// </summary>
    public static JsonObject? CreateJson(ItemKind kind, string name) => kind switch
    {
        // V2 角色卡骨架：spec 前缀 "chara_card" 是 TypeDetector 的首要识别特征
        ItemKind.Character => new JsonObject
        {
            ["spec"] = "chara_card_v2",
            ["spec_version"] = "2.0",
            ["data"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = "",
                ["personality"] = "",
                ["scenario"] = "",
                ["first_mes"] = "",
                ["mes_example"] = "",
                ["tags"] = new JsonArray(),
            },
        },

        // ST 内部世界书：entries 为对象容器（uid 键）。空对象即满足 HasEntryShape 的识别条件
        ItemKind.Lorebook => new JsonObject { ["entries"] = new JsonObject() },

        // OpenAI 对话预设：prompts + prompt_order 骨架（prompts 数组是识别特征）
        ItemKind.Preset => new JsonObject
        {
            ["name"] = name,
            ["temperature"] = 1,
            ["prompts"] = new JsonArray(
                new JsonObject
                {
                    ["identifier"] = "main",
                    ["name"] = "主提示词",
                    ["system_prompt"] = true,
                    ["marker"] = true,
                }),
            ["prompt_order"] = new JsonArray(
                new JsonObject
                {
                    ["character_id"] = 100001,
                    ["order"] = new JsonArray(
                        new JsonObject
                        {
                            ["identifier"] = "main",
                            ["enabled"] = true,
                        }),
                }),
        },

        // 美化主题（官方 power-user.js themeProperties 字段名）：≥2 个特征键即命中识别
        ItemKind.Theme => new JsonObject
        {
            ["name"] = name,
            ["main_text_color"] = "rgba(220,220,220,1)",
            ["italics_text_color"] = "rgba(220,220,220,0.5)",
            ["quote_text_color"] = "rgba(255,213,79,0.8)",
            ["blur_tint_color"] = "rgba(23,23,23,0.7)",
            ["shadow_color"] = "rgba(0,0,0,0.5)",
            ["blur_strength"] = 1,
            ["avatar_style"] = 0,
            ["chat_display"] = 0,
            ["fast_ui_mode"] = true,
            ["movingUI"] = false,
            ["custom_css"] = "",
        },

        // 正则脚本（官方 regex/ 目录格式）：scriptName + findRegex 是识别特征
        ItemKind.Script => new JsonObject
        {
            ["scriptName"] = name,
            ["findRegex"] = "",
            ["replaceString"] = "",
            ["trimStrings"] = new JsonArray(),
            ["placement"] = new JsonArray(2),
            ["disabled"] = false,
            ["markdownOnly"] = false,
            ["promptOnly"] = false,
        },

        // text 走纯文本；archive/other 不支持新建
        _ => null,
    };

    /// <summary>纯文本模板。text 为空文本（用户从零写起）；其余类型返回 null。</summary>
    public static string? CreateText(ItemKind kind, string name) => kind switch
    {
        ItemKind.Text => "",
        _ => null,
    };
}
