using System.Text.Json.Nodes;
using TavernVault.Core;
using TavernVault.Core.Detection;
using TavernVault.Core.FileOps;
using TavernVault.Core.Models;
using TavernVault.Core.Storage;

namespace TavernVault.Core.Tests;

/// <summary>
/// v0.6.0 新建文件：模板→识别回路（硬验收：ContentTemplates 生成的文件必须能被
/// 自家 TypeDetector 识别回对应 kind）、扩展名映射与 SanitizeFileName 边界。
/// </summary>
public class TemplatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TemplatesTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // 10 个 JSON 模板类型（text 走纯文本，archive/other 不支持新建）
    public static readonly string[] JsonKindKeys =
    [
        "character", "lorebook", "preset", "textgen", "instruct",
        "context", "sysprompt", "quickreplies", "theme", "script",
    ];

    public static IEnumerable<object[]> JsonKindKeys_Rows() => JsonKindKeys.Select(k => new object[] { k });

    private static ItemKind KindOf(string key) => ItemKindText.All.First(a => a.Key == key).Kind;

    // ---------- 硬验收：模板 → TypeDetector 识别回路 ----------

    [Theory, MemberData(nameof(JsonKindKeys_Rows))]
    public void CreateJson_IsDetectedBack_AsSameKind(string key)
    {
        var kind = KindOf(key);
        var json = ContentTemplates.CreateJson(kind, "测试名称");
        Assert.NotNull(json);
        Assert.Equal(kind, TypeDetector.DetectJson(json!));
    }

    [Fact]
    public void CreateJson_SerializesAndReparses_StillDetected()
    {
        // 写盘路径是 ToJsonString 后再解析，序列化往返不得破坏识别特征
        foreach (var key in JsonKindKeys)
        {
            var kind = KindOf(key);
            var text = ContentTemplates.CreateJson(kind, "名称")!.ToJsonString();
            var reparsed = Assert.IsType<JsonObject>(JsonNode.Parse(text));
            Assert.Equal(kind, TypeDetector.DetectJson(reparsed));
        }
    }

    [Fact]
    public void Scan_Classifies_CreatedTemplates_ByContent()
    {
        // 端到端：模板落盘 → 扫描入索引 → kind 一致（含 .txt 空文本）
        foreach (var key in JsonKindKeys)
            File.WriteAllText(Path.Combine(_dir, $"模板-{key}.json"),
                ContentTemplates.CreateJson(KindOf(key), "测试名称")!.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "模板-text.txt"), ContentTemplates.CreateText(ItemKind.Text, "测试名称"));

        var vault = new Vault(new SettingsStore(Path.Combine(_dir, "data")));
        vault.AddRoot(_dir);
        vault.Rescan();

        foreach (var key in JsonKindKeys)
        {
            var item = Assert.Single(vault.Items, i => i.FileName == $"模板-{key}.json");
            Assert.Equal(KindOf(key), item.Kind);
        }
        var textItem = Assert.Single(vault.Items, i => i.FileName == "模板-text.txt");
        Assert.Equal(ItemKind.Text, textItem.Kind);
    }

    // ---------- name 注入语义字段 ----------

    [Fact]
    public void CreateJson_InjectsName_IntoSemanticField()
    {
        var card = ContentTemplates.CreateJson(ItemKind.Character, "新角色卡")!;
        Assert.Equal("新角色卡", card["data"]!["name"]!.GetValue<string>());

        var preset = ContentTemplates.CreateJson(ItemKind.Preset, "新预设")!;
        Assert.Equal("新预设", preset["name"]!.GetValue<string>());

        var qr = ContentTemplates.CreateJson(ItemKind.QuickReplies, "新快捷回复")!;
        Assert.Equal("新快捷回复", qr["name"]!.GetValue<string>());

        var regex = ContentTemplates.CreateJson(ItemKind.Script, "新正则")!;
        Assert.Equal("新正则", regex["scriptName"]!.GetValue<string>());
    }

    // ---------- 扩展名映射 / 纯文本 ----------

    [Fact]
    public void ExtensionFor_JsonKinds_MapToDotJson()
    {
        foreach (var key in JsonKindKeys)
            Assert.Equal(".json", ContentTemplates.ExtensionFor(KindOf(key)));
    }

    [Fact]
    public void ExtensionFor_Text_IsTxt_UnknownKinds_AreNull()
    {
        Assert.Equal(".txt", ContentTemplates.ExtensionFor(ItemKind.Text));
        Assert.Null(ContentTemplates.ExtensionFor(ItemKind.Archive));
        Assert.Null(ContentTemplates.ExtensionFor(ItemKind.Other));
    }

    [Fact]
    public void CreateText_TextIsEmptyString_OthersNull()
    {
        Assert.Equal("", ContentTemplates.CreateText(ItemKind.Text, "任意名"));
        Assert.Null(ContentTemplates.CreateText(ItemKind.Character, "任意名"));
        Assert.Null(ContentTemplates.CreateText(ItemKind.Other, "任意名"));
    }

    [Fact]
    public void CreateJson_TextAndUnsupported_ReturnNull()
    {
        Assert.Null(ContentTemplates.CreateJson(ItemKind.Text, "任意名"));
        Assert.Null(ContentTemplates.CreateJson(ItemKind.Archive, "任意名"));
        Assert.Null(ContentTemplates.CreateJson(ItemKind.Other, "任意名"));
    }

    // ---------- SanitizeFileName 边界（create 端点同款清洗） ----------

    [Fact]
    public void SanitizeFileName_StripsInvalidCharacters()
    {
        // 连续非法字符（?"）各产生一个分段 → 双下划线
        var cleaned = FileOperations.SanitizeFileName("a/b\\c:d*e?\"f<g>h|i");
        Assert.Equal("a_b_c_d_e__f_g_h_i", cleaned);
    }

    [Fact]
    public void SanitizeFileName_PathTraversal_CollapsesToSingleSegment()
    {
        var cleaned = FileOperations.SanitizeFileName("..\\..\\evil");
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), cleaned);
        Assert.DoesNotContain("/", cleaned);
        Assert.Equal(".._.._evil", cleaned);
    }

    [Theory]
    [InlineData("name.", "name")]
    [InlineData("name  ", "name")]
    [InlineData("...", "")]
    [InlineData("..", "")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void SanitizeFileName_TrailingDotsAndSpaces_AreTrimmed(string input, string expected)
    {
        Assert.Equal(expected, FileOperations.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", FileOperations.SanitizeFileName(null));
        Assert.Equal("新 角色卡", FileOperations.SanitizeFileName("新 角色卡"));
    }
}
