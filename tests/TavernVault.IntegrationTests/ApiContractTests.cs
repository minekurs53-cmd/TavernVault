using System.Text.Json.Nodes;
using TavernVault.Core.Cards;
using TavernVault.Core.Models;
using Xunit;

namespace TavernVault.IntegrationTests;

/// <summary>
/// 核心合同回归（v0.7.4 收编自冒烟的代表性场景）：真实 Kestrel + 隔离临时库，
/// 每个 API 面至少一条正向 + 一条负向。全量穷举仍归 tests/smoke_api.py（发布前跑），
/// 两者的分工见 docs/development-handoff.md §8。
/// </summary>
[Collection("api")]
public class MetaAndSecurityTests(ApiFixture F)
{
    [Fact]
    public async Task Meta_HasVersionDataDirAndThreeLibraries()
    {
        var meta = await F.Get("/api/meta");
        Assert.Matches(@"^\d+\.\d+", (string)meta["version"]!);
        Assert.Equal(F.DataDir, (string)meta["dataDir"]!);
        var libs = (JsonArray)meta["libraries"]!;
        Assert.Equal(["normal", "tavernST", "tavernTT"], libs.Select(l => (string)l!["key"]!));
    }

    [Fact]
    public async Task Rescan_ReturnsCount()
    {
        var r = await F.Post("/api/rescan");
        Assert.True((int)r["count"]! >= 0);
    }

    [Fact]
    public async Task Api_WithoutOrWrongToken_401()
    {
        var (s1, _) = await F.Call(HttpMethod.Get, "/api/meta", token: "");
        Assert.Equal(401, s1);
        var (s2, body2) = await F.Call(HttpMethod.Get, "/api/meta", token: "DEADBEEFDEADBEEF");
        Assert.Equal(401, s2);
        Assert.Contains("令牌", (string)body2!["error"]!);
    }

    [Fact]
    public async Task SpoofedHost_403()
    {
        var (status, body) = await F.Call(HttpMethod.Get, "/api/meta", host: "evil.example.com");
        Assert.Equal(403, status);
        Assert.Contains("Host", (string)body!["error"]!);
    }

    [Fact]
    public async Task StaticPage_ServedWithoutToken()
    {
        using var resp = await F.Client.GetAsync("index.html");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }
}

[Collection("api")]
public class TextAndCreateTests(ApiFixture F)
{
    public static readonly string[] CreateKinds = ["character", "lorebook", "preset", "theme", "script", "text"];

    [Fact]
    public async Task Create_SixKinds_RoundtripDetection()
    {
        foreach (var kind in CreateKinds)
        {
            var r = await F.Post("/api/items/create", new { kind, name = $"集成-{kind}" });
            Assert.True((bool)r["ok"]!);
            var item = await F.Get($"/api/items/{r["id"]}");
            Assert.Equal(kind, (string)item["kind"]!);
        }
    }

    [Fact]
    public async Task Create_InvalidKindOrRoot_400()
    {
        var (s1, _) = await F.Call(HttpMethod.Post, "/api/items/create", new { kind = "archive", name = "x" });
        Assert.Equal(400, s1);
        var (s2, _) = await F.Call(HttpMethod.Post, "/api/items/create", new { kind = "textgen", name = "x" }); // v0.6.1 已回撤
        Assert.Equal(400, s2);
        var (s3, _) = await F.Call(HttpMethod.Post, "/api/items/create",
            new { kind = "text", name = "x", root = @"D:\不存在的根\nope" });
        Assert.Equal(400, s3);
    }

    [Fact]
    public async Task Text_PutGetSaveAs_Roundtrip()
    {
        var r = await F.Post("/api/items/create", new { kind = "text", name = "集成文本" });
        var id = (string)r["id"]!;
        await F.Put($"/api/text/{id}", new { content = "第一版" });
        Assert.Equal("第一版", (string)(await F.Get($"/api/text/{id}"))["content"]!);

        var saveas = await F.Post($"/api/text/{id}/saveas", new { content = "另存内容" });
        Assert.True((bool)saveas["ok"]!);
        Assert.Contains("-副本", (string)saveas["fileName"]!);
        var copy = await F.Get($"/api/text/{saveas["id"]}");
        Assert.Equal("另存内容", (string)copy["content"]!);
    }
}

[Collection("api")]
public class CardsTests(ApiFixture F)
{
    // 1x1 透明 PNG
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static JsonObject CardJson(string name) => new()
    {
        ["spec"] = "chara_card_v2",
        ["spec_version"] = "2.0",
        ["data"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = "集成描述",
            ["personality"] = "",
            ["scenario"] = "",
            ["first_mes"] = "",
            ["mes_example"] = "",
        },
    };

    private async Task<string> MakeCard(string name)
    {
        var path = Path.Combine(F.TestRoot, $"{name}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(PngBase64));
        CharacterCardFile.Save(path, CardJson(name)); // 嵌入 chara+ccv3
        await F.Post("/api/rescan");
        var items = await F.GetArray($"/api/items?kind=character&q={Uri.EscapeDataString(name)}");
        return (string)items[0]!["id"]!;
    }

    [Fact]
    public async Task Card_GetPut_Roundtrip()
    {
        var id = await MakeCard("集成卡");
        var card = await F.Get($"/api/cards/{id}");
        Assert.Equal("集成卡", (string)card["card"]!["data"]!["name"]!);

        await F.Put($"/api/cards/{id}", new { fields = new { description = "改过的描述" } });
        var after = await F.Get($"/api/cards/{id}");
        Assert.Equal("改过的描述", (string)after["card"]!["data"]!["description"]!);
    }

    [Fact]
    public async Task Card_StaleModified_409()
    {
        var id = await MakeCard("并发卡");
        var stale = (string)(await F.Get($"/api/items/{id}"))["modifiedAt"]!;
        File.SetLastWriteTime(Path.Combine(F.TestRoot, "并发卡.png"), DateTime.Now.AddHours(-1));
        var (status, body) = await F.Call(HttpMethod.Put, $"/api/cards/{id}",
            new { fields = new { description = "不应写入" }, expectedModified = stale });
        Assert.Equal(409, status);
        Assert.Contains("已被外部", (string)body!["error"]!);
    }
}

[Collection("api")]
public class LoreTests(ApiFixture F)
{
    [Fact]
    public async Task Lore_ObjectAndArray_ContainersPreserved()
    {
        File.WriteAllText(Path.Combine(F.TestRoot, "对象书.json"),
            """{"name":"对象书","entries":{"0":{"key":["词"],"content":"内容","comment":"","disable":false,"order":1,"position":0}}}""");
        File.WriteAllText(Path.Combine(F.TestRoot, "数组书.json"),
            """{"name":"数组书","entries":[{"keys":["词A"],"content":"内容A","enabled":true,"insertion_order":10,"id":7,"extensions":{}}]}""");
        await F.Post("/api/rescan");

        var objItems = await F.GetArray("/api/items?kind=lorebook&q=" + Uri.EscapeDataString("对象书"));
        var objLore = await F.Get($"/api/lore/{objItems[0]!["id"]}");
        Assert.Equal("object", (string)objLore["container"]!);
        var (putObjStatus, _) = await F.Call(HttpMethod.Put, $"/api/lore/{objItems[0]!["id"]}",
            new { entries = objLore["entries"], container = "object" });
        Assert.Equal(200, putObjStatus);
        var onDisk = JsonNode.Parse(File.ReadAllText(Path.Combine(F.TestRoot, "对象书.json")))!.AsObject();
        Assert.IsType<JsonObject>(onDisk["entries"]); // 容器保形

        var arrItems = await F.GetArray("/api/items?kind=lorebook&q=" + Uri.EscapeDataString("数组书"));
        var arrLore = await F.Get($"/api/lore/{arrItems[0]!["id"]}");
        Assert.Equal("array", (string)arrLore["container"]!);
        arrLore["entries"]![0]!["data"]!["content"] = "内容A改";
        await F.Put($"/api/lore/{arrItems[0]!["id"]}", new { entries = arrLore["entries"], container = "array" });
        var arrOnDisk = JsonNode.Parse(File.ReadAllText(Path.Combine(F.TestRoot, "数组书.json")))!.AsObject();
        Assert.IsType<JsonArray>(arrOnDisk["entries"]); // 数组容器不被改写
        Assert.Equal("内容A改", (string)arrOnDisk["entries"]![0]!["content"]!);
        Assert.Equal(7, (int)arrOnDisk["entries"]![0]!["id"]!); // raw 字段保留
    }
}

[Collection("api")]
public class TavernGuardTests(ApiFixture F)
{
    [Fact]
    public async Task TavernSource_Edit403_ExportOk_RenameNeedsForce()
    {
        Directory.CreateDirectory(F.TavernRoot);
        File.WriteAllText(Path.Combine(F.TavernRoot, "酒馆卡.json"),
            """{"spec":"chara_card_v2","spec_version":"2.0","data":{"name":"酒馆卡","description":"原描述"}}""");
        File.WriteAllText(Path.Combine(F.TavernRoot, "酒馆书.json"), """{"entries":{}}""");
        await F.Post("/api/roots", new { path = F.TavernRoot, source = "tavernST" });
        await F.Post("/api/rescan");
        var card = (await F.GetArray("/api/items?source=tavernST&kind=character"))[0]!;
        var lore = (await F.GetArray("/api/items?source=tavernST&kind=lorebook"))[0]!;
        var cardId = (string)card["id"]!;
        var loreId = (string)lore["id"]!;

        // 就地编辑退役（v0.7.1）：三路 PUT 一律 403
        var (c1, _) = await F.Call(HttpMethod.Put, $"/api/cards/{cardId}", new { fields = new { description = "x" } });
        var (c2, _) = await F.Call(HttpMethod.Put, $"/api/text/{loreId}", new { content = "{}" });
        var (c3, _) = await F.Call(HttpMethod.Put, $"/api/lore/{loreId}", new { entries = lore["entryCount"], container = "object" });
        Assert.Equal(403, c1);
        Assert.Equal(403, c2);
        Assert.Equal(403, c3);

        // 重命名/移动护栏
        var (r1, _) = await F.Call(HttpMethod.Post, $"/api/items/{cardId}/rename", new { name = "改名" });
        Assert.Equal(403, r1);

        // 导出副本：落第一个局外库根，副本可编辑
        var exp = await F.Post($"/api/items/{cardId}/export", null);
        Assert.Contains("-副本", (string)exp["fileName"]!);
        var copyId = (string)exp["id"]!;
        var (putStatus, _) = await F.Call(HttpMethod.Put, $"/api/cards/{copyId}", new { fields = new { description = "副本可编辑" } });
        Assert.Equal(200, putStatus);

        // 局外源无需导出
        var normalCards = await F.GetArray("/api/items?source=normal&kind=character");
        if (normalCards.Count > 0)
        {
            var (s, _) = await F.Call(HttpMethod.Post, $"/api/items/{normalCards[0]!["id"]}/export", null);
            Assert.Equal(400, s);
        }
    }
}

[Collection("api")]
public class CollectApiTests(ApiFixture F)
{
    [Fact]
    public async Task PreviewAndExecute_ClassifyCopyMoveAndGuards()
    {
        var source = Path.Combine(Path.GetTempPath(), "tavernvault-it-collect-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(source, "深层"));
        File.WriteAllText(Path.Combine(source, "卡A.json"),
            """{"spec":"chara_card_v2","spec_version":"2.0","data":{"name":"收纳卡A","description":""}}""");
        File.WriteAllText(Path.Combine(source, "深层", "书A.json"), """{"entries":{}}""");
        File.WriteAllText(Path.Combine(source, "忽略.zip"), "PK");
        try
        {
            var preview = await F.Post("/api/collect/preview", new { source });
            var groups = (JsonArray)preview["groups"]!;
            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, g => (string)g!["kind"]! == "character" && g!["files"]!.AsArray().Count == 1);
            Assert.Contains((preview["skipped"] as JsonArray)!, s => (string)s!["name"]! == "忽略.zip");

            var run = await F.Post("/api/collect", new { source, root = F.TestRoot });
            Assert.True((bool)run["ok"]!);
            Assert.Equal(2, (int)run["copied"]!);
            Assert.True(File.Exists(Path.Combine(F.TestRoot, "角色卡", "卡A.json")));
            Assert.True(File.Exists(Path.Combine(F.TestRoot, "世界书", "书A.json"))); // 嵌套文件平铺进类型子目录
            Assert.True(File.Exists(Path.Combine(source, "卡A.json"))); // 默认源不动

            // 重名 → " (2)"
            await F.Post("/api/collect", new { source, root = F.TestRoot });
            Assert.True(File.Exists(Path.Combine(F.TestRoot, "角色卡", "卡A (2).json")));

            // move 模式：源文件进回收站
            File.WriteAllText(Path.Combine(source, "移动我.txt"), "x");
            var run2 = await F.Post("/api/collect", new { source, root = F.TestRoot, move = true, files = new[] { "移动我.txt" } });
            Assert.True((bool)run2["ok"]!);
            Assert.False(File.Exists(Path.Combine(source, "移动我.txt")));
            Assert.True(File.Exists(Path.Combine(F.TestRoot, "文本", "移动我.txt")));

            // 负向：未知清单 / 未登记根 / 酒馆源目标
            var (n1, _) = await F.Call(HttpMethod.Post, "/api/collect",
                new { source, root = F.TestRoot, files = new[] { "不存在.txt" } });
            Assert.Equal(400, n1);
            var (n2, _) = await F.Call(HttpMethod.Post, "/api/collect",
                new { source, root = @"D:\不存在的根\nope" });
            Assert.Equal(400, n2);
            Directory.CreateDirectory(Path.Combine(source, "酒馆假根"));
            await F.Post("/api/roots", new { path = Path.Combine(source, "酒馆假根"), source = "tavernST" });
            var (n3, _) = await F.Call(HttpMethod.Post, "/api/collect", new { source, root = Path.Combine(source, "酒馆假根") });
            Assert.Equal(400, n3);
            await F.Delete("/api/roots", new { path = Path.Combine(source, "酒馆假根") });
        }
        finally
        {
            try { Directory.Delete(source, recursive: true); } catch { }
        }
    }
}

[Collection("api")]
public class BackupAndHistoryTests(ApiFixture F)
{
    [Fact]
    public async Task BackupRestore_HistoryAndDeletedFilter()
    {
        var r = await F.Post("/api/items/create", new { kind = "text", name = "历史文本" });
        var id = (string)r["id"]!;
        await F.Put($"/api/text/{id}", new { content = "第一版" });
        await F.Put($"/api/text/{id}", new { content = "第二版" });

        var backups = await F.GetArray($"/api/items/{id}/backups"); // 顶层数组
        Assert.Equal(2, backups.Count);

        // 还原最新的备份 → 内容回到"第一版"（最新备份 = 第二次写入前的状态）
        var newest = backups.MaxBy(b => (string)b!["savedAt"]!);
        var restore = await F.Post($"/api/backups/{newest!["id"]}/restore", null);
        Assert.True((bool)restore["ok"]!);
        Assert.Equal("第一版", (string)(await F.Get($"/api/text/{id}"))["content"]!);

        // 修改历史包含该文件；删除后按"原文件不存在"过滤
        var history = await F.Get("/api/history");
        Assert.Contains((history["rows"] as JsonArray)!, x => (string)x!["fileName"]! == "历史文本.txt");
        await F.Post($"/api/items/{id}/delete", null);
        var history2 = await F.Get("/api/history");
        Assert.DoesNotContain((history2["rows"] as JsonArray)!, x => (string)x!["fileName"]! == "历史文本.txt");
    }
}
