using TavernVault.Core.Models;

namespace TavernVault.Core.Detection;

/// <summary>检测本机 SillyTavern / TauriTavern 安装目录。不包含任何机器特定路径：优先环境变量，回退用户目录约定。</summary>
public static class TavernDetector
{
    // v0.6.0 扩充对齐官方 src/constants.js 的 USER_DIRECTORY_TEMPLATE：
    // instruct / context / sysprompt / QuickReplies 与官方一致；
    // 文本补全预设官方目录名为 "TextGen Settings"（键 textGen_Settings）——
    // handoff §3.14 写作 "TextGeneration Settings" 系笔误，两者都探测以免漏接旧约定。
    public static readonly string[] Subdirs =
    [
        "characters", "worlds", "OpenAI Settings", "themes", "regex",
        "TextGen Settings", "TextGeneration Settings", "instruct", "context", "sysprompt", "QuickReplies",
    ];

    /// <summary>
    /// 检测酒馆数据目录。候选顺序：环境变量（TV_SILLYTAVERN_DATA / TV_TAURITAVERN_DATA）
    /// → 用户目录约定（%USERPROFILE%\SillyTavern\data\default-user 等）。校验需含 characters 子目录。
    /// </summary>
    public static string? DetectSillyTavern()
        => Detect("TV_SILLYTAVERN_DATA",
            Path.Combine(UserHome, "SillyTavern", "data", "default-user"));

    public static string? DetectTauriTavern()
        => Detect("TV_TAURITAVERN_DATA",
            Path.Combine(UserHome, "TauriTavern", "cache", "default-user"));

    private static string UserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? Detect(string envVar, string fallback)
    {
        foreach (var dir in new[] { Environment.GetEnvironmentVariable(envVar), fallback })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (!Directory.Exists(dir)) continue;
            if (!Directory.Exists(Path.Combine(dir, "characters"))) continue;
            return dir;
        }
        return null;
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
