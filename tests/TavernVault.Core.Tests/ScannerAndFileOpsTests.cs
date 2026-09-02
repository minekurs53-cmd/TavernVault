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

    [Fact]
    public void Scan_Skips_ReparsePoint_Dirs()
    {
        // junction 指向库外目录：其中的文件不得进入索引（否则库外文件可被应用改删，圣域被击穿）
        var outside = Path.Combine(_dir, "outside-sensitive");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "外部.json"), LorebookJson());

        var library = Path.Combine(_dir, "库内");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "正常.json"), LorebookJson());
        var link = Path.Combine(library, "链接");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{outside}\"")
        { CreateNoWindow = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi);
        p!.WaitForExit(5000);
        if (p.ExitCode != 0) return; // 环境不允许创建 junction 时跳过断言

        var vault = new Vault(new SettingsStore(_dir + "-data"));
        vault.AddRoot(library);
        vault.Rescan();

        Assert.Single(vault.Items); // 只有"正常.json"
        Assert.DoesNotContain(vault.Items, i => i.FileName == "外部.json");
    }

    [Fact]
    public void Scan_Cleans_Card_Title()
    {
        // Title 会派生导出文件名，必须清洗控制符并限长（v0.5.1）
        var name = "第一行\n第二行" + new string('名', 300);
        Write("标题卡.json", new JsonObject
        {
            ["spec"] = "chara_card_v2",
            ["data"] = new JsonObject { ["name"] = name, ["description"] = "d" },
        }.ToJsonString());

        var vault = new Vault(new SettingsStore(_dir + "-data"));
        vault.AddRoot(_dir);
        vault.Rescan();

        var item = vault.Items.Single();
        Assert.True(item.Title!.Length <= 201); // 200 字符 + 省略号
        Assert.DoesNotContain('\n', item.Title);
    }

    [Fact]
    public void CorruptSettings_Preserves_Index_And_Warns()
    {
        // P1-1 回归：settings.json 损坏时不得让启动期自愈把 index.json 清空（收藏/标签会永久丢失）
        var data = _dir + "-data3";
        var store = new SettingsStore(data);
        var vault = new Vault(store);
        var root = Path.Combine(_dir, "根");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "书.json"), LorebookJson());
        vault.AddRoot(root);
        vault.Rescan();
        var id = vault.Items.Single().Id;
        vault.SetFavorite(id, true);
        File.WriteAllText(Path.Combine(store.DataDir, "settings.json"), "{corrupt!!");

        var again = new Vault(new SettingsStore(data)); // 模拟重启
        Assert.NotNull(again.SettingsWarning);
        Assert.Empty(again.Settings.LibraryRoots);
        Assert.True(again.Items.Single().Favorite); // 索引未被"自愈"覆盖
        Assert.True(File.Exists(Directory.GetFiles(store.DataDir, "settings.json.corrupt-*").Single()));
        Assert.True(File.Exists(Path.Combine(store.DataDir, "index.bak"))); // 索引留档可用
    }
}
