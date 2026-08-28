using System.Text.Json.Nodes;
using TavernVault.Core.FileOps;
using TavernVault.Core.Models;
using TavernVault.Core.Scanning;
using TavernVault.Core.Storage;

namespace TavernVault.Core.Tests;

public class ScannerAndFileOpsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public ScannerAndFileOpsTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string relName, string content)
    {
        var path = Path.Combine(_dir, relName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string LorebookJson(int entries = 3)
    {
        var map = new JsonObject();
        for (int i = 0; i < entries; i++)
            map[i.ToString()] = new JsonObject { ["key"] = new JsonArray($"词{i}"), ["content"] = $"内容{i}" };
        return new JsonObject { ["entries"] = map }.ToJsonString();
    }

    [Fact]
    public void Scan_Classifies_ByContent()
    {
        Write("世界书/a.json", LorebookJson());
        Write("世界书/其实是卡.json", new JsonObject
        {
            ["spec"] = "chara_card_v2",
            ["data"] = new JsonObject { ["name"] = "卡", ["description"] = "d" },
        }.ToJsonString());
        Write("预设/p.json", new JsonObject { ["prompts"] = new JsonArray(), ["temperature"] = 1 }.ToJsonString());
        File.WriteAllBytes(Path.Combine(_dir, "misc.zip"), [0x50, 0x4B, 3, 4]);

        var vault = new Vault(new SettingsStore(_dir + "-data"));
        vault.AddRoot(_dir);
        var count = vault.Rescan();

        Assert.Equal(4, count);
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Lorebook && i.EntryCount == 3);
        var card = vault.Items.First(i => i.FileName == "其实是卡.json");
        Assert.Equal(ItemKind.Character, card.Kind);
        Assert.Equal("卡", card.Title);
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Preset);
        Assert.Contains(vault.Items, i => i.Kind == ItemKind.Archive);
    }

    [Fact]
    public void Scan_Preserves_UserData()
    {
        Write("a.json", LorebookJson());
        var vault = new Vault(new SettingsStore(_dir + "-data"));
        vault.AddRoot(_dir);
        vault.Rescan();
        var item = vault.Items.Single();
        vault.SetFavorite(item.Id, true);
        vault.SetUserTags(item.Id, ["常用"]);

        vault.Rescan(); // 重扫后用户数据仍在
        var again = vault.Items.Single();
        Assert.True(again.Favorite);
        Assert.Equal(["常用"], again.UserTags);
    }

    [Fact]
    public void Rename_And_Move_Work()
    {
        var path = Write("旧名.json", LorebookJson());
        var scanner = new LibraryScanner();
        var items = scanner.Scan([_dir], new Dictionary<string, LibraryItem>());
        var item = items.Single();

        var renamed = FileOperations.Rename(item, "新名");
        Assert.True(File.Exists(renamed));
        Assert.Equal("新名.json", Path.GetFileName(renamed));

        Directory.CreateDirectory(Path.Combine(_dir, "子目录"));
        item.FullPath = renamed;
        item.FileName = Path.GetFileName(renamed);
        var moved = FileOperations.Move(item, _dir, "子目录");
        Assert.Equal(Path.Combine(_dir, "子目录", "新名.json"), moved);
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public void Guard_Rejects_Path_Outside_Roots()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => FileOperations.GuardUnderRoots(@"C:\Windows\system32\evil.json", [_dir]));
        FileOperations.GuardUnderRoots(Path.Combine(_dir, "世界书", "a.json"), [_dir]); // 不抛
    }

    [Fact]
    public void ComputeId_Stable_IgnoringCase()
    {
        Assert.Equal(
            LibraryScanner.ComputeId(@"D:\Foo\Bar.json".ToUpperInvariant()),
            LibraryScanner.ComputeId(@"d:\foo\bar.json"));
    }
}
