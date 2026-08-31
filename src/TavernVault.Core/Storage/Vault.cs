using TavernVault.Core.Models;
using TavernVault.Core.Scanning;

namespace TavernVault.Core.Storage;

/// <summary>条目查询参数。</summary>
public class QueryParams
{
    public ItemKind? Kind { get; set; }
    public string? Search { get; set; }
    public string? UserTag { get; set; }
    public bool? Favorite { get; set; }
    public string? Dir { get; set; }
    public string Sort { get; set; } = "name"; // name | modified | size | kind
}

/// <summary>
/// 库的内存索引 + 设置。所有 UI 查询都走这里；重扫描后自动持久化。
/// </summary>
public sealed class Vault
{
    private readonly object _lock = new();
    private readonly SettingsStore _store;
    private readonly LibraryScanner _scanner = new();

    /// <summary>文件级备份（编辑/还原前自动备份）。</summary>
    public BackupStore Backups { get; }

    public AppSettings Settings { get; private set; }
    public DateTime LastScanAt { get; private set; }

    public Vault(SettingsStore? store = null)
    {
        _store = store ?? new SettingsStore();
        Settings = _store.LoadSettings();
        Items = _store.LoadIndex();
        Backups = new BackupStore(_store.DataDir) { MaxPerFile = Settings.MaxBackupsPerFile };
        Backups.RetentionFor = path => RetentionFor(RootContaining(path));
    }

    private int RetentionFor(LibraryRoot? root)
        => root?.Source == LibrarySource.TavernTT ? 10 : Math.Max(1, Settings.MaxBackupsPerFile);

    /// <summary>若开启自动备份或文件属于酒馆来源，在覆盖写入前调用；失败不抛出。</summary>
    public void BackupBeforeWrite(string fullPath)
    {
        var root = RootContaining(fullPath);
        var isTavern = root is not null && root.Source != LibrarySource.Normal;
        if (!Settings.AutoBackup && !isTavern) return;
        Backups.MaxPerFile = RetentionFor(root);
        Backups.BackupBeforeWrite(fullPath);
    }

    public List<LibraryItem> Items { get; private set; } = [];

    /// <summary>重扫全部库目录。返回条目数。</summary>
    public int Rescan()
    {
        List<LibraryItem> items;
        lock (_lock)
        {
            var existing = Items.ToDictionary(i => i.Id, i => i);
            items = _scanner.Scan(Settings.LibraryRoots, existing);
            Items = items;
            LastScanAt = DateTime.Now;
            _store.SaveIndex(Items);
        }
        return items.Count;
    }

    public LibraryItem? Find(string id)
    {
        lock (_lock) return Items.FirstOrDefault(i => i.Id == id);
    }

    public List<LibraryItem> Query(QueryParams p)
    {
        lock (_lock)
        {
            IEnumerable<LibraryItem> q = Items;

            if (p.Kind is { } kind) q = q.Where(i => i.Kind == kind);
            if (p.Favorite is { } fav) q = q.Where(i => i.Favorite == fav);
            if (!string.IsNullOrEmpty(p.UserTag))
                q = q.Where(i => i.UserTags.Contains(p.UserTag, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(p.Dir))
                q = q.Where(i => i.RelativeDir.Equals(p.Dir, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(p.Search))
            {
                var needle = p.Search.Trim();
                q = q.Where(i =>
                    i.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    (i.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (i.Creator?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (i.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    i.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                    i.UserTags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
            }

            q = p.Sort switch
            {
                "modified" => q.OrderByDescending(i => i.ModifiedAt),
                "size" => q.OrderByDescending(i => i.SizeBytes),
                "kind" => q.OrderBy(i => i.Kind).ThenBy(i => i.DisplayName, StringComparer.CurrentCulture),
                _ => q.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            };
            return q.ToList();
        }
    }

    public (List<string> tags, int count) AllUserTags()
    {
        lock (_lock)
        {
            var tags = Items.SelectMany(i => i.UserTags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();
            return (tags, Items.Count);
        }
    }

    public bool SetFavorite(string id, bool fav)
    {
        lock (_lock)
        {
            var item = Items.FirstOrDefault(i => i.Id == id);
            if (item is null) return false;
            item.Favorite = fav;
            _store.SaveIndex(Items);
            return true;
        }
    }

    public bool SetUserTags(string id, List<string> tags)
    {
        lock (_lock)
        {
            var item = Items.FirstOrDefault(i => i.Id == id);
            if (item is null) return false;
            item.UserTags = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _store.SaveIndex(Items);
            return true;
        }
    }

    /// <summary>读取收藏 + 用户标签快照（重命名/移动前调用）。</summary>
    public (bool Favorite, List<string> Tags) GetUserData(string id)
    {
        lock (_lock)
        {
            var item = Items.FirstOrDefault(i => i.Id == id);
            return item is null ? (false, []) : (item.Favorite, [.. item.UserTags]);
        }
    }

    /// <summary>把快照应用到新条目（重命名/移动后 Id 变化时迁移用户数据）。</summary>
    public bool SetUserData(string id, bool favorite, List<string> tags)
    {
        lock (_lock)
        {
            var item = Items.FirstOrDefault(i => i.Id == id);
            if (item is null) return false;
            item.Favorite = favorite;
            item.UserTags = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _store.SaveIndex(Items);
            return true;
        }
    }

    public void AddRoot(LibraryRoot root)
    {
        var full = Path.GetFullPath(root.Path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        lock (_lock)
        {
            if (!Settings.LibraryRoots.Any(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)))
                Settings.LibraryRoots.Add(new LibraryRoot { Path = full, Source = root.Source });
            _store.SaveSettings(Settings);
        }
    }

    public void AddRoot(string path) => AddRoot(new LibraryRoot { Path = path, Source = LibrarySource.Normal });

    public bool RemoveRoot(string path)
    {
        lock (_lock)
        {
            var full = Path.GetFullPath(path);
            var removed = Settings.LibraryRoots.RemoveAll(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) _store.SaveSettings(Settings);
            return removed;
        }
    }

    public LibraryRoot? FindRoot(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
        return Settings.LibraryRoots.FirstOrDefault(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    public LibrarySource SourceOf(string rootPath)
        => FindRoot(rootPath)?.Source ?? LibrarySource.Normal;

    /// <summary>查找包含指定文件的库根（最长前缀匹配）。</summary>
    public LibraryRoot? RootContaining(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        LibraryRoot? best = null;
        foreach (var r in Settings.LibraryRoots)
        {
            var rp = r.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(rp, StringComparison.OrdinalIgnoreCase) &&
                (best is null || r.Path.Length > best.Path.Length))
                best = r;
        }
        return best;
    }

    public void SaveSettings() => _store.SaveSettings(Settings);
}
