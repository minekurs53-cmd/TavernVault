using TavernVault.Core.Collect;
using TavernVault.Core.FileOps;
using TavernVault.Core.Models;

namespace TavernVault.Core.Tests;

/// <summary>
/// 收纳入库（v0.7.3）：来源扫描的内容识别分类（与主扫描同规则）、子目录映射、同名唯一化。
/// </summary>
public class CollectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public CollectTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_Classifies_MixedMessyFolder()
    {
        File.WriteAllText(Path.Combine(_dir, "卡A.json"),
            """{"spec":"chara_card_v2","spec_version":"2.0","data":{"name":"卡A","description":""}}""");
        File.WriteAllText(Path.Combine(_dir, "书A.json"), """{"entries":{}}""");
        File.WriteAllText(Path.Combine(_dir, "预设A.json"),
            """{"name":"P","prompts":[{"identifier":"main"}],"prompt_order":[]}""");
        File.WriteAllText(Path.Combine(_dir, "美化A.json"),
            """{"main_text_color":"rgba(0,0,0,1)","blur_strength":1}""");
        File.WriteAllText(Path.Combine(_dir, "脚本A.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(_dir, "说明.txt"), "hello");
        File.WriteAllText(Path.Combine(_dir, "归档.zip"), "PK");
        File.WriteAllText(Path.Combine(_dir, "坏.json"), "{not json");
        Directory.CreateDirectory(Path.Combine(_dir, ".hidden"));
        File.WriteAllText(Path.Combine(_dir, ".hidden", "藏.json"), """{"entries":{}}""");
        Directory.CreateDirectory(Path.Combine(_dir, "子目录"));
        File.WriteAllText(Path.Combine(_dir, "子目录", "卡B.json"),
            """{"spec":"chara_card_v2","spec_version":"2.0","data":{"name":"卡B","description":""}}""");

        var found = CollectScanner.Scan(_dir);

        Assert.Equal(9, found.Count); // .hidden 点目录不递归
        Assert.Contains(found, c => c.Kind == ItemKind.Character && c.RelativePath == "卡A.json");
        Assert.Contains(found, c => c.Kind == ItemKind.Character
            && c.RelativePath == Path.Combine("子目录", "卡B.json")); // 递归 + 相对路径保留
        Assert.Contains(found, c => c.Kind == ItemKind.Lorebook && c.RelativePath == "书A.json");
        Assert.Contains(found, c => c.Kind == ItemKind.Preset && c.RelativePath == "预设A.json");
        Assert.Contains(found, c => c.Kind == ItemKind.Theme && c.RelativePath == "美化A.json");
        Assert.Contains(found, c => c.Kind == ItemKind.Script && c.RelativePath == "脚本A.js");
        Assert.Contains(found, c => c.Kind == ItemKind.Text && c.RelativePath == "说明.txt");
        Assert.Contains(found, c => c.Kind == ItemKind.Text && c.RelativePath == "坏.json"); // 坏 JSON 兜底为文本
        Assert.Contains(found, c => c.Kind == ItemKind.Archive && c.RelativePath == "归档.zip"); // 收纳时跳过
    }

    [Fact]
    public void SubdirFor_MapsCollectibleKinds_OthersNull()
    {
        Assert.Equal("角色卡", CollectScanner.SubdirFor(ItemKind.Character));
        Assert.Equal("世界书", CollectScanner.SubdirFor(ItemKind.Lorebook));
        Assert.Equal("预设", CollectScanner.SubdirFor(ItemKind.Preset));
        Assert.Equal("美化", CollectScanner.SubdirFor(ItemKind.Theme));
        Assert.Equal("脚本", CollectScanner.SubdirFor(ItemKind.Script));
        Assert.Equal("文本", CollectScanner.SubdirFor(ItemKind.Text));
        Assert.Null(CollectScanner.SubdirFor(ItemKind.Archive)); // 不收纳
        Assert.Null(CollectScanner.SubdirFor(ItemKind.Other));
    }

    [Fact]
    public void UniqueDestinationPath_NumbersOnCollision_AndSanitizes()
    {
        var target = Path.Combine(_dir, "收");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "1");

        // 已占用 → " (2)"
        Assert.Equal(Path.Combine(target, "a (2).txt"), FileOperations.UniqueDestinationPath(target, "a.txt"));
        // 连占两次 → " (3)"
        File.WriteAllText(Path.Combine(target, "a (2).txt"), "2");
        Assert.Equal(Path.Combine(target, "a (3).txt"), FileOperations.UniqueDestinationPath(target, "a.txt"));
        // 不存在的名字：原名直用
        Assert.Equal(Path.Combine(target, "新.txt"), FileOperations.UniqueDestinationPath(target, "新.txt"));
        // 非法字符先清洗（注意用非盘符语法的非法字符："b:c" 会被 Path 解析为 B 盘相对路径）
        Assert.Equal(Path.Combine(target, "b_d.txt"), FileOperations.UniqueDestinationPath(target, "b<d.txt"));
    }
}
