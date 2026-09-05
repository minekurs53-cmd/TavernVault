using TavernVault.Core.Cards;
using TavernVault.Core.Models;

namespace TavernVault.Core.Collect;

/// <summary>收纳入库的候选文件：来源目录里的一个文件及其识别结果。</summary>
public sealed record CollectCandidate(string FullPath, string RelativePath, string FileName, ItemKind Kind, long SizeBytes);

/// <summary>
/// 「收纳入库」的来源扫描（v0.7.3）：递归枚举散乱文件夹，按**文件内容**识别类型，
/// 与主扫描同一套识别规则（TypeDetector / PNG 内嵌卡复核）。只读，不改任何文件。
/// </summary>
public static class CollectScanner
{
    /// <summary>可收纳类型 → 目标子目录名（与酒馆功能分区命名对齐）。archive/other 返回 null = 建议跳过。</summary>
    public static string? SubdirFor(ItemKind kind) => kind switch
    {
        ItemKind.Character => "角色卡",
        ItemKind.Lorebook => "世界书",
        ItemKind.Preset => "预设",
        ItemKind.Theme => "美化",
        ItemKind.Script => "脚本",
        ItemKind.Text => "文本",
        _ => null,
    };

    /// <summary>递归扫描来源目录（跳过点目录）。单个文件识别失败不影响整体。</summary>
    public static List<CollectCandidate> Scan(string sourceDir)
    {
        var root = Path.GetFullPath(sourceDir);
        var result = new List<CollectCandidate>();
        foreach (var path in EnumerateFiles(root))
        {
            try
            {
                var info = new FileInfo(path);
                result.Add(new CollectCandidate(
                    path,
                    Path.GetRelativePath(root, path),
                    info.Name,
                    ClassifyKind(path),
                    info.Length));
            }
            catch
            {
                // 单文件读不动（锁/权限）：跳过，不阻断整体扫描
            }
        }
        return result.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
            yield return f;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.Length > 0 && name[0] == '.') continue; // 点目录跳过（与主扫描同规则）
            foreach (var f in EnumerateFiles(sub))
                yield return f;
        }
    }

    /// <summary>仅识别类型（不复用 LibraryScanner.Classify：那边还回填标题/描述等索引字段）。</summary>
    private static ItemKind ClassifyKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".png")
                return CharacterCardFile.Load(path) is not null ? ItemKind.Character : ItemKind.Other;
            if (ext == ".json")
            {
                return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject obj
                    ? Detection.TypeDetector.DetectJson(obj)
                    : ItemKind.Text;
            }
            return Detection.TypeDetector.DetectByExtension(path, out _);
        }
        catch
        {
            return ext == ".json" ? ItemKind.Text : ItemKind.Other; // 与主扫描的失败兜底同规则
        }
    }
}
