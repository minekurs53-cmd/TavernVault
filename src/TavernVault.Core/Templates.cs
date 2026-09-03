using System.Text.Json.Nodes;
using TavernVault.Core.Models;

namespace TavernVault.Core;

/// <summary>
/// 「新建文件」空白模板（v0.6.0）。
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
            or ItemKind.TextGenPreset or ItemKind.InstructTemplate or ItemKind.ContextTemplate
            or ItemKind.SysPrompt or ItemKind.QuickReplies or ItemKind.Theme
            or ItemKind.Script => ".json",
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

        // 文本补全预设（官方 TextGen Settings/）：temp + rep_pen 是核心识别特征，
        // 其余为常用默认值（字段名均为官方原样，如 typical_p 而非 typical）
        ItemKind.TextGenPreset => new JsonObject
        {
            ["temp"] = 1,
            ["rep_pen"] = 1.1,
            ["top_k"] = 40,
            ["top_p"] = 0.95,
            ["top_a"] = 0,
            ["min_p"] = 0.05,
            ["typical_p"] = 1,
            ["tfs"] = 1,
            ["mirostat_mode"] = 0,
            ["mirostat_tau"] = 5,
            ["mirostat_eta"] = 0.1,
            ["add_bos_token"] = true,
            ["ban_eos_token"] = false,
            ["skip_special_tokens"] = true,
            ["temperature_last"] = false,
        },

        // 指令模板（官方 instruct/，ChatML 风格）：≥2 个序列字段即命中识别
        ItemKind.InstructTemplate => new JsonObject
        {
            ["name"] = name,
            ["input_sequence"] = "<|im_start|>user\n",
            ["output_sequence"] = "<|im_start|>assistant\n",
            ["system_sequence"] = "<|im_start|>system\n",
            ["stop_sequence"] = "<|im_end|>",
            ["input_suffix"] = "\n",
            ["output_suffix"] = "\n",
            ["wrap"] = true,
            ["macro"] = true,
            ["names_behavior"] = "force",
        },

        // 上下文模板（官方 context/）：story_string 最具区分性，可单独命中识别。
        // char 在官方模板中为字符串列表（注入占位符），这里与官方一致给空数组
        ItemKind.ContextTemplate => new JsonObject
        {
            ["name"] = name,
            ["story_string"] = "{{#if system}}{{system}}\n{{/if}}{{#if description}}{{description}}\n{{/if}}{{trim}}",
            ["char"] = new JsonArray(),
            ["example_separator"] = "***",
            ["chat_start"] = "***",
            ["chat_end"] = "***",
        },

        // 系统提示模板（官方 sysprompt/Blank.json 同款三键；post_history 是与脚本区分的精确特征）
        ItemKind.SysPrompt => new JsonObject
        {
            ["name"] = name,
            ["content"] = "",
            ["post_history"] = "",
        },

        // 快捷回复（官方 QuickReplies/ 的 v2 序列化结构）：qrList 数组是识别特征
        ItemKind.QuickReplies => new JsonObject
        {
            ["version"] = 2,
            ["name"] = name,
            ["qrList"] = new JsonArray(),
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
