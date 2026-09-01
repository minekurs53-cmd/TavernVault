using System.Text.Json.Nodes;
using TavernVault.Core.Models;
using TavernVault.Core.Storage;

namespace TavernVault.Core.Tests;

public class VaultQueryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-vq-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dataDir;
    private readonly string _normalRoot;
    private readonly string _stRoot;
    private readonly string _ttRoot;

    public VaultQueryTests()
    {
        _dataDir = Path.Combine(_dir, "data");
        _normalRoot = Path.Combine(_dir, "root-normal");
        _stRoot = Path.Combine(_dir, "root-st");
        _ttRoot = Path.Combine(_dir, "root-tt");
        Directory.CreateDirectory(_dataDir);
        foreach (var d in new[] { _normalRoot, _stRoot, _ttRoot }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string root, string relName, string content)
    {
        var path = Path.Combine(root, relName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string LorebookJson() => new JsonObject
    {
        ["entries"] = new JsonObject { ["0"] = new JsonObject { ["key"] = "词", ["content"] = "内容" } },
    }.ToJsonString();

    private static string CardJson() => new JsonObject
    {
        ["spec"] = "chara_card_v2",
        ["data"] = new JsonObject { ["name"] = "卡", ["description"] = "d" },
    }.ToJsonString();

    private Vault NewVault()
    {
        // 固定文件集：normal(卡+书) / st(卡+书) / tt(书)
        Write(_normalRoot, "世界书/a.json", LorebookJson());
        Write(_normalRoot, "角色卡/b.json", CardJson());
        Write(_stRoot, "世界书/c.json", LorebookJson());
        Write(_stRoot, "角色卡/d.json", CardJson());
        Write(_ttRoot, "世界书/e.json", LorebookJson());

        var vault = NewVaultNoSeed();
        vault.AddRoot(new LibraryRoot { Path = _normalRoot, Source = LibrarySource.Normal });
        vault.AddRoot(new LibraryRoot { Path = _stRoot, Source = LibrarySource.TavernST });
        vault.AddRoot(new LibraryRoot { Path = _ttRoot, Source = LibrarySource.TavernTT });
        vault.Rescan();
        return vault;
    }

    private Vault NewVaultNoSeed() => new(new SettingsStore(_dataDir));

    [Fact]
    public void Query_Filters_By_Source_Exclusively()
    {
        var vault = NewVault();
        Assert.Equal(2, vault.Query(new QueryParams { Source = LibrarySource.Normal }).Count);
        Assert.Equal(2, vault.Query(new QueryParams { Source = LibrarySource.TavernST }).Count);
        Assert.Single(vault.Query(new QueryParams { Source = LibrarySource.TavernTT }));
        Assert.Equal(5, vault.Query(new QueryParams()).Count); // Source=null 回归全量
    }

    [Fact]
    public void Query_Source_And_RootPath_Are_And_Cross_Library_Is_Empty()
    {
        var vault = NewVault();
        // A 库 source + B 库 root：必须 0 条
        Assert.Empty(vault.Query(new QueryParams { Source = LibrarySource.TavernST, RootPath = Path.GetFullPath(_normalRoot) }));
        Assert.Empty(vault.Query(new QueryParams { Source = LibrarySource.Normal, RootPath = Path.GetFullPath(_stRoot) }));
        // 同库 source + root：AND 命中
        var st = vault.Query(new QueryParams { Source = LibrarySource.TavernST, RootPath = Path.GetFullPath(_stRoot) });
        Assert.Equal(2, st.Count);
    }

    [Fact]
    public void Query_Source_Combines_With_Kind_And_Dir()
    {
        var vault = NewVault();
        var loreInNormal = vault.Query(new QueryParams
        { Source = LibrarySource.Normal, Kind = ItemKind.Lorebook, Dir = "世界书" });
        Assert.Single(loreInNormal);
        Assert.All(loreInNormal, i => Assert.Equal(ItemKind.Lorebook, i.Kind));
        // st 根的同名目录不应混入（source 过滤生效）
        Assert.DoesNotContain(loreInNormal, i => i.RootPath == Path.GetFullPath(_stRoot));
    }

    [Fact]
    public void BuildLibraries_Aggregates_And_Holds_Invariants()
    {
        var vault = NewVault();
        vault.SetUserTags(vault.Items.First(i => i.RootSource == LibrarySource.TavernST).Id, ["常用"]);
        vault.SetFavorite(vault.Items.First(i => i.RootSource == LibrarySource.Normal).Id, true);

        var (allTags, total) = vault.AllUserTags();
        var libs = vault.BuildLibraries();

        Assert.Equal(3, libs.Count);
        Assert.Equal(["normal", "tavernST", "tavernTT"], libs.Select(l => l.Key));
        Assert.Equal(["局外存储", "SillyTavern", "TauriTavern"], libs.Select(l => l.Label));
        // 不变量：全局 total 与 kinds == 三库之和
        Assert.Equal(total, libs.Sum(l => l.Total));
        Assert.All(libs, l => Assert.Equal(8, l.Kinds.Count)); // 8 类全列含 0
        Assert.All(libs, l => Assert.Equal(l.Total, l.Kinds.Sum(k => k.Count))); // 库内不变量
        // rootCount = 注册根数量（非资源数量）
        Assert.All(libs, l => Assert.Equal(1, l.RootCount));
        Assert.Equal(1, libs[0].Favorites);
        Assert.Equal(0, libs[1].Favorites);
        Assert.Contains(libs[1].Tags, t => t.Tag == "常用");
        Assert.DoesNotContain(libs[0].Tags, t => t.Tag == "常用");
        // 普通库 dirs 跨根聚合（root=null），酒馆库 dirs 含空根占位
        Assert.All(libs[0].Dirs, d => Assert.Null(d.Root));
        Assert.All(libs[1].Dirs, d => Assert.Equal(Path.GetFullPath(_stRoot), d.Root));
    }

    [Fact]
    public void Constructor_Heals_Source_Drift_From_Stale_Index()
    {
        // 模拟冷升级：同一目录先以 Normal 扫描并持久化索引，随后根来源改为 TavernST。
        // 新建 Vault（--server 冷启动不主动重扫的等价场景）必须自愈 RootSource。
        Write(_stRoot, "世界书/c.json", LorebookJson());
        Write(_stRoot, "角色卡/d.json", CardJson());
        var vault1 = NewVaultNoSeed();
        vault1.AddRoot(new LibraryRoot { Path = _stRoot, Source = LibrarySource.Normal });
        vault1.Rescan();
        Assert.All(vault1.Items, i => Assert.Equal(LibrarySource.Normal, i.RootSource));

        vault1.RemoveRoot(_stRoot);
        vault1.AddRoot(new LibraryRoot { Path = _stRoot, Source = LibrarySource.TavernST });

        var vault2 = NewVaultNoSeed(); // 读旧索引 → 发现 RootSource 漂移 → 构造内 Rescan
        Assert.NotEmpty(vault2.Items);
        Assert.All(vault2.Items, i => Assert.Equal(LibrarySource.TavernST, i.RootSource));
        Assert.Equal(LibrarySource.TavernST,
            vault2.Query(new QueryParams { Source = LibrarySource.TavernST }).First().RootSource);
    }
}
