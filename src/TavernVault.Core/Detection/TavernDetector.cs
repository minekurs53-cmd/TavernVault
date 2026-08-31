using TavernVault.Core.Models;

namespace TavernVault.Core.Detection;

/// <summary>检测本机 SillyTavern / TauriTavern 安装目录。</summary>
public static class TavernDetector
{
    public static readonly string[] Subdirs =
        ["characters", "worlds", "OpenAI Settings", "themes", "regex"];

    /// <summary>检测 SillyTavern 数据目录（D:\agent\SillyTavern\data\default-user\）。</summary>
    public static string? DetectSillyTavern()
    {
        var baseDir = @"D:\agent\SillyTavern\data\default-user";
        if (!Directory.Exists(baseDir)) return null;
        if (!Directory.Exists(Path.Combine(baseDir, "characters"))) return null;
        return baseDir;
    }

    /// <summary>检测 TauriTavern 缓存目录（D:\agent\TauriTavern\cache\default-user\）。</summary>
    public static string? DetectTauriTavern()
    {
        var baseDir = @"D:\agent\TauriTavern\cache\default-user";
        if (!Directory.Exists(baseDir)) return null;
        if (!Directory.Exists(Path.Combine(baseDir, "characters"))) return null;
        return baseDir;
    }

    /// <summary>返回检测到的所有酒馆及其可注册子目录。</summary>
    public static List<(LibrarySource source, string label, string baseDir, List<string> subdirs)> DetectAll()
    {
        var result = new List<(LibrarySource, string, string, List<string>)>();

        var st = DetectSillyTavern();
        if (st is not null)
            result.Add((LibrarySource.TavernST, "SillyTavern", st, ExistingSubdirs(st)));

        var tt = DetectTauriTavern();
        if (tt is not null)
            result.Add((LibrarySource.TavernTT, "TauriTavern", tt, ExistingSubdirs(tt)));

        return result;
    }

    /// <summary>为指定酒馆基目录生成 LibraryRoot 列表。</summary>
    public static List<LibraryRoot> BuildRoots(string baseDir, LibrarySource source)
    {
        var roots = new List<LibraryRoot>();
        foreach (var sub in Subdirs)
        {
            var full = Path.Combine(baseDir, sub);
            if (Directory.Exists(full))
                roots.Add(new LibraryRoot { Path = full, Source = source });
        }
        return roots;
    }

    private static List<string> ExistingSubdirs(string baseDir)
    {
        return Subdirs.Where(s => Directory.Exists(Path.Combine(baseDir, s))).ToList();
    }
}
