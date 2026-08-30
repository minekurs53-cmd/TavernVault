using System.Text;
using System.Text.Json.Nodes;
using TavernVault.Core.Cards;
using TavernVault.Core.Models;
using TavernVault.Core.Scanning;
using TavernVault.Core.Storage;

namespace TavernVault.Core.Tests;

/// <summary>A 计划：角色卡内嵌世界书（data.character_book）读写 + 相关修复回归。</summary>
public class CharacterBookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public CharacterBookTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static JsonObject SpecCard(JsonObject book) => new()
    {
        ["spec"] = "chara_card_v2",
        ["data"] = new JsonObject
        {
            ["name"] = "带书卡",
            ["description"] = "描述",
            ["character_book"] = book,
        },
    };

    private static JsonObject SpecEntry(string keyword, string content, bool enabled = true) => new()
    {
        ["keys"] = new JsonArray(keyword),
        ["secondary_keys"] = new JsonArray(),
        ["content"] = content,
        ["comment"] = "条目备注",
        ["constant"] = false,
        ["selective"] = true,
        ["use_regex"] = false,
        ["id"] = 42,
        ["enabled"] = enabled,
        ["insertion_order"] = 50,
        ["position"] = "before_char",
        ["extensions"] = new JsonObject { ["depth"] = 3, ["use_probability"] = 80 },
    };

    // ---------- Spec 格式：读取与规范 ----------

    [Fact]
    public void Read_SpecEntry_Normalizes_To_St_Format()
    {
        var book = new JsonObject { ["entries"] = new JsonArray(SpecEntry("触发", "内容A")) };
        var entries = CharacterBook.ReadEntries(book);

        var e = entries.Single();
        Assert.Equal("0", e.MapKey); // 数组容器 → 索引字符串
        Assert.NotNull(e.Raw);
        Assert.Equal("触发", e.St["key"]![0]!.GetValue<string>());
        Assert.False(e.St["disable"]!.GetValue<bool>());
        Assert.Equal(50, e.St["order"]!.GetValue<int>());
        Assert.Equal(0, e.St["position"]!.GetValue<int>());       // before_char → 0
        Assert.Equal(3, e.St["depth"]!.GetValue<int>());
        Assert.Equal(80, e.St["probability"]!.GetValue<int>());
    }

    [Fact]
    public void Write_SpecEntry_Merges_Edits_And_Preserves_Unknown_Fields()
    {
        var book = new JsonObject { ["entries"] = new JsonArray(SpecEntry("触发", "内容A")) };
        var entries = CharacterBook.ReadEntries(book);
        entries[0].St["content"] = "编辑后内容";
        entries[0].St["disable"] = true;
        entries[0].St["position"] = 1; // after_char

        CharacterBook.WriteEntries(book, entries);

        var written = book["entries"]![0]!.AsObject();
        Assert.Equal("编辑后内容", written["content"]?.GetValue<string>());
        Assert.False(written["enabled"]!.GetValue<bool>());          // disable=true → enabled=false
        Assert.Equal("after_char", written["position"]?.GetValue<string>()); // 保持字符串形态
        Assert.Equal(50, written["insertion_order"]!.GetValue<int>());
        // 未编辑字段原样保留
        Assert.Equal(42, written["id"]!.GetValue<int>());
        Assert.True(written["selective"]!.GetValue<bool>());
        Assert.False(written["use_regex"]!.GetValue<bool>());
        Assert.Equal(3, written["extensions"]!["depth"]!.GetValue<int>());
        Assert.Equal(80, written["extensions"]!["use_probability"]!.GetValue<int>());
        Assert.Equal(new JsonArray("触发").ToJsonString(), written["keys"]!.ToJsonString());
    }

    // ---------- ST 格式：透传保形 ----------

    [Fact]
    public void Read_And_Write_StFormat_Entries_Keeps_Shape_And_Extra_Fields()
    {
        var stEntry = new JsonObject
        {
            ["key"] = new JsonArray("词"),
            ["keysecondary"] = new JsonArray(),
            ["content"] = "ST内容",
            ["comment"] = "备注",
            ["constant"] = true,
            ["disable"] = true,
            ["order"] = 10,
            ["position"] = 4,
            ["depth"] = 2,
            ["probability"] = 50,
            ["ignoreBudget"] = true, // ST 特有字段
        };
        // 容器为对象（dict）
        var book = new JsonObject { ["entries"] = new JsonObject { ["0"] = stEntry } };

        var entries = CharacterBook.ReadEntries(book);
        Assert.Null(entries[0].Raw); // ST 格式原样编辑
        Assert.True(entries[0].St["disable"]!.GetValue<bool>());
        Assert.Equal(4, entries[0].St["position"]!.GetValue<int>());

        entries[0].St["content"] = "改过的内容";
        CharacterBook.WriteEntries(book, entries);

        // 容器保持对象形态
        Assert.IsAssignableFrom<JsonObject>(book["entries"]);
        var written = book["entries"]!["0"]!.AsObject();
        Assert.Equal("改过的内容", written["content"]?.GetValue<string>());
        Assert.True(written["disable"]!.GetValue<bool>());   // 格式未翻转
        Assert.Equal(4, written["position"]!.GetValue<int>());
        Assert.True(written["ignoreBudget"]!.GetValue<bool>()); // 未知字段保留
    }

    // ---------- 混合格式 / 计数 / 新建 ----------

    [Fact]
    public void Mixed_Format_Entries_Each_Keeps_Own_Format()
    {
        var book = new JsonObject
        {
            ["entries"] = new JsonArray(SpecEntry("a", "内容1"), new JsonObject
            {
                ["key"] = new JsonArray("b"),
                ["content"] = "内容2",
                ["disable"] = false,
                ["order"] = 1,
                ["position"] = 0,
            }),
        };
        var entries = CharacterBook.ReadEntries(book);
        Assert.NotNull(entries[0].Raw);
        Assert.Null(entries[1].Raw);

        entries[1].St["content"] = "内容2改";
        CharacterBook.WriteEntries(book, entries);

        Assert.True(book["entries"]![0]!.AsObject().ContainsKey("keys"));     // 仍是 Spec
        Assert.False(book["entries"]![1]!.AsObject().ContainsKey("keys"));    // 仍是 ST
        Assert.Equal("内容2改", book["entries"]![1]!.AsObject()["content"]?.GetValue<string>());
    }

    [Fact]
    public void Count_And_Create()
    {
        var arrBook = new JsonObject { ["entries"] = new JsonArray(SpecEntry("a", "x")) };
        var mapBook = new JsonObject { ["entries"] = new JsonObject { ["0"] = SpecEntry("a", "x") } };
        Assert.Equal(1, CharacterBook.CountEntries(arrBook));
        Assert.Equal(1, CharacterBook.CountEntries(mapBook));

        var fresh = CharacterBook.CreateBook();
        Assert.Equal(0, CharacterBook.CountEntries(fresh));
        Assert.IsAssignableFrom<JsonArray>(fresh["entries"]);
    }

    // ---------- 端到端：文件保存后内嵌书完整 ----------

    [Fact]
    public void Card_File_Save_Preserves_Embedded_Book()
    {
        var path = Path.Combine(_dir, "card.json");
        var card = SpecCard(new JsonObject { ["name"] = "内置书", ["entries"] = new JsonArray(SpecEntry("触发", "内容A")) });
        File.WriteAllText(path, card.ToJsonString(), Encoding.UTF8);

        var loaded = CharacterCardFile.Load(path)!.AsObject();
        var data = CharacterCardFile.GetDataNode(loaded);
        Assert.True(CharacterBook.HasBook(data));

        var book = data["character_book"]!.AsObject();
        var entries = CharacterBook.ReadEntries(book);
        entries[0].St["content"] = "保存后的内容";
        CharacterBook.WriteEntries(book, entries);
        CharacterCardFile.Save(path, loaded);

        var reread = CharacterCardFile.GetDataNode(CharacterCardFile.Load(path)!.AsObject());
        var writtenEntry = reread["character_book"]!["entries"]![0]!.AsObject();
        Assert.Equal("保存后的内容", writtenEntry["content"]?.GetValue<string>());
        Assert.Equal(42, writtenEntry["id"]!.GetValue<int>());
        Assert.True(writtenEntry["selective"]!.GetValue<bool>());
    }

    // ---------- 回归：JSON 卡片根级镜像字段同步 ----------

    [Fact]
    public void Save_Syncs_Legacy_Root_Mirror_Fields()
    {
        var path = Path.Combine(_dir, "st-export.json");
        // ST JSON 导出格式：根级 + data 双份字段
        var root = SpecCard(new JsonObject());
        root["name"] = "旧名字";
        root["description"] = "旧描述";
        root["first_mes"] = "旧开场";
        File.WriteAllText(path, root.ToJsonString(), Encoding.UTF8);

        var loaded = CharacterCardFile.Load(path)!.AsObject();
        CharacterCardFile.GetDataNode(loaded)["description"] = "新描述";
        CharacterCardFile.Save(path, loaded);

        var reread = CharacterCardFile.Load(path)!.AsObject();
        Assert.Equal("新描述", reread["description"]?.GetValue<string>());   // 镜像已同步
        Assert.Equal("带书卡", reread["name"]?.GetValue<string>());          // 镜像一律以 data 为准（旧不一致值被纠正）

        // 清空 data 字段 → 镜像一并移除
        var loaded2 = CharacterCardFile.Load(path)!.AsObject();
        CharacterCardFile.GetDataNode(loaded2).Remove("description");
        CharacterCardFile.Save(path, loaded2);
        var reread2 = CharacterCardFile.Load(path)!.AsObject();
        Assert.False(reread2.ContainsKey("description"));
    }

    // ---------- 回归：扫描识别内嵌书 ----------

    [Fact]
    public void Scan_Detects_Embedded_Book()
    {
        var path = Path.Combine(_dir, "card-with-book.json");
        File.WriteAllText(path, SpecCard(new JsonObject
        {
            ["entries"] = new JsonArray(SpecEntry("a", "1"), SpecEntry("b", "2")),
        }).ToJsonString(), Encoding.UTF8);

        var vault = new Vault(new SettingsStore(_dir + "-data"));
        vault.AddRoot(_dir);
        vault.Rescan();

        var item = vault.Items.Single();
        Assert.Equal(ItemKind.Character, item.Kind);
        Assert.True(item.HasCharacterBook);
        Assert.Equal(2, item.EntryCount);
    }

    // ---------- 回归：增量扫描复用未变化条目 ----------

    [Fact]
    public void Rescan_Reuses_Unchanged_Items()
    {
        var path = Path.Combine(_dir, "a.json");
        File.WriteAllText(path, SpecCard(new JsonObject()).ToJsonString(), Encoding.UTF8);

        var vault = new Vault(new SettingsStore(_dir + "-data"));
        vault.AddRoot(_dir);
        vault.Rescan();
        var first = vault.Items.Single();

        // 未修改文件再次扫描 → 同一实例（跳过重新解析）
        vault.Rescan();
        Assert.Same(first, vault.Items.Single());

        // 修改文件后 → 重新解析
        Thread.Sleep(20); // 确保修改时间变化
        File.WriteAllText(path, SpecCard(new JsonObject { ["entries"] = new JsonArray(SpecEntry("x", "y")) }).ToJsonString(), Encoding.UTF8);
        vault.Rescan();
        var second = vault.Items.Single();
        Assert.NotSame(first, second);
        Assert.Equal(1, second.EntryCount);
    }

    // ---------- 回归：用户数据迁移 ----------

    [Fact]
    public void UserData_Migrates_After_Rename()
    {
        var path = Path.Combine(_dir, "old-name.json");
        File.WriteAllText(path, SpecCard(new JsonObject()).ToJsonString(), Encoding.UTF8);

        var vault = new Vault(new SettingsStore(_dir + "-data"));
        vault.AddRoot(_dir);
        vault.Rescan();
        var item = vault.Items.Single();
        vault.SetFavorite(item.Id, true);
        vault.SetUserTags(item.Id, ["常用"]);

        // 模拟应用内重命名流程：快照 → 改名 → 重扫 → 迁移
        var snap = vault.GetUserData(item.Id);
        var newPath = TavernVault.Core.FileOps.FileOperations.Rename(item, "new-name");
        vault.Rescan();
        var newId = LibraryScanner.ComputeId(newPath);
        vault.SetUserData(newId, snap.Favorite, snap.Tags);

        var migrated = vault.Items.Single();
        Assert.True(migrated.Favorite);
        Assert.Equal(["常用"], migrated.UserTags);
    }
}
