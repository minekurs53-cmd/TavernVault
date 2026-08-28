using System.Text;
using System.Text.Json.Nodes;
using TavernVault.Core.Cards;
using TavernVault.Core.Detection;
using TavernVault.Core.Models;

namespace TavernVault.Core.Tests;

public class CardAndDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public CardAndDetectionTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static JsonObject V2Card(string name = "测试卡") => new()
    {
        ["spec"] = "chara_card_v2",
        ["spec_version"] = "2.0",
        ["data"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = "{{char}}是一个测试角色",
            ["personality"] = "冷静",
            ["scenario"] = "测试场景",
            ["first_mes"] = "你好。",
            ["mes_example"] = "",
            ["creator"] = "tester",
            ["character_version"] = "1.0",
            ["tags"] = new JsonArray("测试", "科幻"),
            ["alternate_greetings"] = new JsonArray("早。"),
            ["extensions"] = new JsonObject(),
        },
    };

    // ---------- 类型识别 ----------

    [Fact]
    public void Detect_V2Card()
    {
        Assert.Equal(ItemKind.Character, TypeDetector.DetectJson(V2Card()));
    }

    [Fact]
    public void Detect_V1Card()
    {
        var v1 = new JsonObject
        {
            ["name"] = "旧卡",
            ["description"] = "描述",
            ["personality"] = "性格",
            ["first_mes"] = "你好",
        };
        Assert.Equal(ItemKind.Character, TypeDetector.DetectJson(v1));
    }

    [Fact]
    public void Detect_Lorebook()
    {
        var lore = new JsonObject
        {
            ["entries"] = new JsonObject
            {
                ["0"] = new JsonObject { ["key"] = new JsonArray("触发"), ["content"] = "内容" },
                ["1"] = new JsonObject { ["key"] = new JsonArray("第二"), ["content"] = "内容2" },
            },
        };
        Assert.Equal(ItemKind.Lorebook, TypeDetector.DetectJson(lore));
    }

    [Fact]
    public void Detect_Preset()
    {
        var preset = new JsonObject
        {
            ["temperature"] = 0.7,
            ["prompts"] = new JsonArray(new JsonObject { ["identifier"] = "main" }),
        };
        Assert.Equal(ItemKind.Preset, TypeDetector.DetectJson(preset));
    }

    [Fact]
    public void Detect_Theme()
    {
        var theme = new JsonObject
        {
            ["name"] = "主题",
            ["custom_css"] = "body{}",
            ["blur_strength"] = 1,
            ["chat_display"] = "bubble",
        };
        Assert.Equal(ItemKind.Theme, TypeDetector.DetectJson(theme));
    }

    [Fact]
    public void Detect_TavernScript()
    {
        var script = new JsonObject
        {
            ["name"] = "脚本",
            ["content"] = "console.log(1)",
            ["id"] = "abc",
            ["type"] = "script",
        };
        Assert.Equal(ItemKind.Script, TypeDetector.DetectJson(script));
    }

    // ---------- PNG 内嵌卡片 ----------

    [Fact]
    public void CardPng_Roundtrip()
    {
        var path = Path.Combine(_dir, "card.png");
        File.WriteAllBytes(path, TestPng.Build());

        Assert.Null(CharacterCardFile.Load(path)); // 还没有卡片数据

        var card = V2Card();
        CharacterCardFile.Save(path, card);

        var loaded = CharacterCardFile.Load(path);
        Assert.NotNull(loaded);
        var data = CharacterCardFile.GetDataNode(loaded!.AsObject());
        Assert.Equal("测试卡", data["name"]?.GetValue<string>());
        Assert.Equal("tester", data["creator"]?.GetValue<string>());

        // 修改后再保存、重读
        data["name"] = "改名后";
        CharacterCardFile.Save(path, loaded.AsObject());
        var reloaded = CharacterCardFile.Load(path)!;
        Assert.Equal("改名后", CharacterCardFile.GetDataNode(reloaded.AsObject())["name"]?.GetValue<string>());
        // ccv3 同步更新
        Assert.NotNull(PngChunkIO.ReadText(path, CharacterCardFile.Ccv3Key));
    }

    [Fact]
    public void CardJson_Roundtrip_PreservesUnknownKeys()
    {
        var path = Path.Combine(_dir, "card.json");
        var root = V2Card();
        root["avatar"] = "none";           // ST 导出格式里的额外键
        root["create_date"] = "2025-1-1";
        File.WriteAllText(path, root.ToJsonString(), Encoding.UTF8);

        var loaded = CharacterCardFile.Load(path);
        Assert.NotNull(loaded);
        CharacterCardFile.GetDataNode(loaded!.AsObject())["description"] = "新描述";
        CharacterCardFile.Save(path, loaded.AsObject());

        var reloaded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("新描述", reloaded["data"]!["description"]?.GetValue<string>());
        Assert.Equal("none", reloaded["avatar"]?.GetValue<string>());       // 未知键保留
        Assert.Equal("2025-1-1", reloaded["create_date"]?.GetValue<string>());
    }

    [Fact]
    public void Card_Load_RejectsPlainJson()
    {
        var path = Path.Combine(_dir, "plain.json");
        File.WriteAllText(path, new JsonObject { ["foo"] = 1 }.ToJsonString());
        Assert.Null(CharacterCardFile.Load(path));
    }
}
