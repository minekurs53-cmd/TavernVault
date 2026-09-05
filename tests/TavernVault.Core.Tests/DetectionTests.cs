using System.Text.Json.Nodes;
using TavernVault.Core.Cards;
using TavernVault.Core.Detection;
using TavernVault.Core.Models;
using TavernVault.Core.Storage;

namespace TavernVault.Core.Tests;

/// <summary>
/// 格式识别回归：
/// v0.6.1 回撤 v0.6.0 新增的 5 类（textgen / instruct / context / sysprompt / quickreplies）——
/// 这些分类缺乏编辑价值且个别规则会误收预设文件，官方模板类 JSON 统一回落
/// "文本"或"脚本"（原文编辑器仍然可用）；主题键名对齐与独立世界书数组容器保形保留。
/// 夹具字段均取自官方仓库真实文件（来源见各用例注释）。
/// </summary>
public class DetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public DetectionTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- 官方夹具（default/content/presets/ 下的真实文件字段） ----

    /// <summary>官方 textgen 预设 Universal-Light.json 的字段子集（字段名为官方原样）。</summary>
    private static JsonObject TextGenPresetJson() => new()
    {
        ["temp"] = 1.25,
        ["temperature_last"] = false,
        ["top_p"] = 1,
        ["top_k"] = 0,
        ["min_p"] = 0.1,
        ["typical_p"] = 1,
        ["tfs"] = 1,
        ["rep_pen"] = 1,
        ["rep_pen_range"] = 0,
        ["smoothing_factor"] = 0,
        ["add_bos_token"] = true,
        ["ban_eos_token"] = false,
        ["skip_special_tokens"] = true,
        ["mirostat_mode"] = 0,
        ["mirostat_tau"] = 5,
        ["mirostat_eta"] = 0.1,
        ["sampler_priority"] = new JsonArray("repetition_penalty", "temperature"),
    };

    /// <summary>官方 instruct 模板 ChatML.json 的字段子集。</summary>
    private static JsonObject InstructTemplateJson() => new()
    {
        ["input_sequence"] = "<|im_start|>user",
        ["output_sequence"] = "<|im_start|>assistant",
        ["last_output_sequence"] = "",
        ["system_sequence"] = "<|im_start|>system",
        ["stop_sequence"] = "<|im_end|>",
        ["wrap"] = true,
        ["macro"] = true,
        ["names_behavior"] = "force",
        ["output_suffix"] = "<|im_end|>\n",
        ["name"] = "ChatML",
    };

    /// <summary>官方 context 模板 Default.json 的字段子集。</summary>
    private static JsonObject ContextTemplateJson() => new()
    {
        ["story_string"] = "{{#if system}}{{system}}\n{{/if}}{{trim}}",
        ["example_separator"] = "***",
        ["chat_start"] = "***",
        ["use_stop_strings"] = false,
        ["names_as_stop_strings"] = true,
        ["story_string_position"] = 0,
        ["story_string_depth"] = 1,
        ["name"] = "Default",
    };

    /// <summary>官方 sysprompt 模板 Blank.json：{name, content, post_history}。</summary>
    private static JsonObject SysPromptJson() => new()
    {
        ["name"] = "Blank",
        ["content"] = "",
        ["post_history"] = "",
    };

    /// <summary>官方快捷回复 v2 集（quick-reply/src/QuickReplySet.js 的序列化结构）。</summary>
    private static JsonObject QuickRepliesJson() => new()
    {
        ["version"] = 2,
        ["name"] = "我的快捷回复",
        ["disableSend"] = false,
        ["placeBeforeInput"] = false,
        ["injectInput"] = false,
        ["qrList"] = new JsonArray(
            new JsonObject
            {
                ["id"] = 1,
                ["label"] = "继续",
                ["showLabel"] = true,
                ["title"] = "",
                ["message"] = "/continue",
                ["contextList"] = new JsonArray(),
                ["isHidden"] = false,
                ["executeOnAi"] = false,
            }),
        ["idIndex"] = 1,
    };

    // ---------- 回撤 5 类后的回落归类（v0.6.1） ----------

    [Fact]
    public void Detect_TextGenPreset_FallsBackToText()
    {
        // 官方 textgen 预设不再设专属分类：采样参数 JSON 归"文本"（原文编辑器可用）
        Assert.Equal(ItemKind.Text, TypeDetector.DetectJson(TextGenPresetJson()));
    }

    [Fact]
    public void Detect_InstructTemplate_FallsBackToText()
    {
        // 官方 instruct 模板无 {name, content} 对 → 归"文本"
        Assert.Equal(ItemKind.Text, TypeDetector.DetectJson(InstructTemplateJson()));
    }

    [Fact]
    public void Detect_ContextTemplate_FallsBackToText()
    {
        // 官方 context 模板（story_string 等）→ 归"文本"
        Assert.Equal(ItemKind.Text, TypeDetector.DetectJson(ContextTemplateJson()));
    }

    [Fact]
    public void Detect_SysPromptFile_CountsAsScript()
    {
        // {name, content, post_history} 含 {name, content} 对 → 与裸两键一样按"脚本"处理
        Assert.Equal(ItemKind.Script, TypeDetector.DetectJson(SysPromptJson()));
    }

    [Fact]
    public void Detect_BareNameContent_StillScript()
    {
        // 裸 {name, content} 与酒馆助手脚本无法区分 → 按脚本
        var json = new JsonObject { ["name"] = "脚本", ["content"] = "console.log(1)" };
        Assert.Equal(ItemKind.Script, TypeDetector.DetectJson(json));
    }

    [Fact]
    public void Detect_QuickReplies_FallsBackToText()
    {
        // 官方快捷回复 v2 集（qrList 数组）→ 归"文本"
        Assert.Equal(ItemKind.Text, TypeDetector.DetectJson(QuickRepliesJson()));
    }

    [Fact]
    public void Detect_PromptsArray_StillPreset()
    {
        // 回撤不影响主分类：prompts 数组（OpenAI 对话预设）恒判"预设"，
        // 即使同时携带大量采样字段（v0.6.0 曾被 textgen 规则误收的场景，规则删除后自然消除）
        var json = TextGenPresetJson();
        json["prompts"] = new JsonArray(new JsonObject { ["identifier"] = "main" });
        Assert.Equal(ItemKind.Preset, TypeDetector.DetectJson(json));
    }

    [Fact]
    public void Detect_SamplerPlusThemeKeys_ThemeWins()
    {
        // textgen 规则删除后，同时含采样字段与 ≥2 主题键的文件按既有主题规则判定
        var json = TextGenPresetJson();
        json["main_text_color"] = "rgba(0,0,0,1)";
        json["blur_strength"] = 1;
        Assert.Equal(ItemKind.Theme, TypeDetector.DetectJson(json));
    }

    // ---------- 主题键名对齐（v0.6.0 保留项） ----------

    [Fact]
    public void Detect_Theme_OfficialColorKeys()
    {
        // 官方 power-user.js themeProperties：italics_text_color / quote_text_color / blur_tint_color
        var theme = new JsonObject
        {
            ["name"] = "主题",
            ["main_text_color"] = "rgba(0,0,0,1)",
            ["italics_text_color"] = "rgba(0,0,0,1)",
            ["quote_text_color"] = "rgba(79,79,79,1)",
            ["blur_tint_color"] = "rgba(23,23,23,1)",
            ["custom_css"] = "",
        };
        Assert.Equal(ItemKind.Theme, TypeDetector.DetectJson(theme));
    }

    [Fact]
    public void Detect_Theme_ObsoleteKeysNoLongerTrigger()
    {
        // 官方不存在的旧键名（italics_color/quote_color）单独出现不再构成主题判定
        var theme = new JsonObject
        {
            ["italics_color"] = "#000",
            ["quote_color"] = "#444",
        };
        Assert.Equal(ItemKind.Text, TypeDetector.DetectJson(theme));
    }

    [Fact]
    public void Detect_Theme_BogusFoldersIsOfficialKey()
    {
        // bogus_folders 经官方源码核实存在（power_user.bogus_folders），参与判定
        var theme = new JsonObject
        {
            ["bogus_folders"] = false,
            ["fast_ui_mode"] = true,
        };
        Assert.Equal(ItemKind.Theme, TypeDetector.DetectJson(theme));
    }

    // ---------- 酒馆子目录清单（v0.6.1 回撤后与官方功能分区一致） ----------

    [Fact]
    public void TavernDetector_Subdirs_MatchOfficialFunctionalDirs()
    {
        Assert.Equal(
            ["characters", "worlds", "OpenAI Settings", "themes", "regex"],
            TavernDetector.Subdirs);
    }

    // ---------- 独立世界书：数组容器保形（复用 CharacterBook 机制，lore 端点同款调用） ----------

    private static JsonObject SpecArrayBookRoot() => new()
    {
        ["name"] = "SpecV2数组书",
        ["description"] = "NovelAI / Spec V2 导出：entries 为数组",
        ["entries"] = new JsonArray(
            new JsonObject
            {
                ["keys"] = new JsonArray("触发甲"),
                ["content"] = "内容甲",
                ["comment"] = "条目甲",
                ["enabled"] = true,
                ["insertion_order"] = 10,
                ["position"] = "before_char",
                ["id"] = 7,
                ["selective"] = true,
                ["use_regex"] = false,
                ["extensions"] = new JsonObject { ["depth"] = 4, ["use_probability"] = 100 },
            },
            new JsonObject
            {
                ["keys"] = new JsonArray("触发乙"),
                ["secondary_keys"] = new JsonArray("次要"),
                ["content"] = "内容乙",
                ["enabled"] = false,
                ["insertion_order"] = 20,
                ["position"] = "after_char",
                ["constant"] = true,
                ["id"] = 9,
                ["extensions"] = new JsonObject(),
            }),
    };

    [Fact]
    public void Lore_ArrayContainer_Roundtrip_PreservesShapeAndRaw()
    {
        var root = SpecArrayBookRoot();
        var beforeSecond = root["entries"]![1]!.DeepClone();

        // 读（= GET /api/lore 对数组容器的转换路径）
        var entries = CharacterBook.ReadEntries(root);
        Assert.Equal(2, entries.Count);
        Assert.NotNull(entries[0].Raw);
        Assert.Equal("触发甲", entries[0].St["key"]![0]!.GetValue<string>());
        Assert.False(entries[0].St["disable"]!.GetValue<bool>()); // Spec enabled=true → ST disable=false
        Assert.True(entries[1].St["disable"]!.GetValue<bool>());
        Assert.Equal(1, entries[1].St["position"]!.GetValue<int>()); // after_char → 1

        // 改一条（编辑器只改 St；raw 原样回传）
        entries[0].St["content"] = "内容甲·改";
        entries[0].St["disable"] = true;
        CharacterBook.WriteEntries(root, entries); // = PUT /api/lore container="array" 的合并路径

        // 容器仍为数组
        var arr = Assert.IsType<JsonArray>(root["entries"]);
        var first = Assert.IsType<JsonObject>(arr[0]);
        var second = Assert.IsType<JsonObject>(arr[1]);

        // 被编辑条目：编辑生效，raw 字段（id/selective/use_regex/extensions）原样保留
        Assert.Equal("内容甲·改", first["content"]?.GetValue<string>());
        Assert.False(first["enabled"]!.GetValue<bool>()); // disable=true → enabled 翻转
        Assert.Equal(7, first["id"]!.GetValue<int>());
        Assert.True(first["selective"]!.GetValue<bool>());
        Assert.False(first["use_regex"]!.GetValue<bool>());
        Assert.Equal("before_char", first["position"]?.GetValue<string>()); // Spec 字符串 position 保形
        Assert.Equal(4, first["extensions"]!["depth"]!.GetValue<int>());

        // 未编辑条目：字节级不变
        Assert.Equal(beforeSecond.ToJsonString(), second.ToJsonString());
    }

    [Fact]
    public void Lore_ArrayContainer_NewAppendedEntry_StaysInArray()
    {
        // 新增条目（无 raw，ST 格式）也写进数组容器，不改变形态
        var root = SpecArrayBookRoot();
        var entries = CharacterBook.ReadEntries(root);
        entries.Add(new CharacterBook.BookEntry
        {
            MapKey = "2",
            St = new JsonObject
            {
                ["key"] = new JsonArray("新词"),
                ["content"] = "新内容",
                ["disable"] = false,
                ["order"] = 100,
                ["position"] = 0,
            },
        });
        CharacterBook.WriteEntries(root, entries);

        var arr = Assert.IsType<JsonArray>(root["entries"]);
        Assert.Equal(3, arr.Count);
        var appended = Assert.IsType<JsonObject>(arr[2]);
        Assert.Equal("新内容", appended["content"]?.GetValue<string>());
    }

    // ---------- 扫描端到端：回撤后落库归类 ----------

    [Fact]
    public void Scan_Classifies_RevertedKinds_ByContent()
    {
        File.WriteAllText(Path.Combine(_dir, "p1.json"), TextGenPresetJson().ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "p2.json"), InstructTemplateJson().ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "p3.json"), ContextTemplateJson().ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "p4.json"), SysPromptJson().ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "p5.json"), QuickRepliesJson().ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "p6.json"), SpecArrayBookRoot().ToJsonString());

        var vault = new Vault(new SettingsStore(Path.Combine(_dir, "data")));
        vault.AddRoot(_dir);
        vault.Rescan();

        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Text && i.FileName == "p1.json");
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Text && i.FileName == "p2.json");
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Text && i.FileName == "p3.json");
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Script && i.FileName == "p4.json");
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Text && i.FileName == "p5.json");
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Lorebook && i.EntryCount == 2); // 数组容器世界书
        Assert.DoesNotContain(vault.Items, i => i.Kind == ItemKind.Preset);
    }
}
