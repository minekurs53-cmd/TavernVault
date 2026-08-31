namespace TavernVault.Core.Models;

/// <summary>逻辑库聚合信息（Vault.BuildLibraries 输出）：按库根来源并集划分的三个库。</summary>
public class LibraryInfo
{
    public string Key { get; set; } = "";      // normal | tavernST | tavernTT
    public string Label { get; set; } = "";    // 局外存储 | SillyTavern | TauriTavern
    public int Total { get; set; }
    /// <summary>已注册库根数量（非资源数量）。</summary>
    public int RootCount { get; set; }
    public int Favorites { get; set; }
    public List<KindCount> Kinds { get; set; } = [];
    public List<DirCount> Dirs { get; set; } = [];
    public List<TagCount> Tags { get; set; } = [];
}

public class KindCount
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>酒馆库：每个注册根一条（Root=根路径，Dir 为空）；普通库：按相对目录跨根聚合（Root 为空）。</summary>
public class DirCount
{
    public string? Root { get; set; }
    public string Dir { get; set; } = "";
    public int Count { get; set; }
}

public class TagCount
{
    public string Tag { get; set; } = "";
    public int Count { get; set; }
}
