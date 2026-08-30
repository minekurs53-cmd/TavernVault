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

    public AppSettings Settings { get; private set; }
    public DateTime LastScanAt { get; private set; }

    public Vault(SettingsStore? store = null)
    {
        _store = store ?? new SettingsStore();
        Settings = _store.LoadSettings();
        Items = _store.LoadIndex();
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

    public void AddRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        lock (_lock)
        {
            if (!Settings.LibraryRoots.Contains(full, StringComparer.OrdinalIgnoreCase))
                Settings.LibraryRoots.Add(full);
            _store.SaveSettings(Settings);
        }
    }

    public bool RemoveRoot(string path)
    {
        lock (_lock)
        {
            var full = Path.GetFullPath(path);
            var removed = Settings.LibraryRoots.RemoveAll(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) _store.SaveSettings(Settings);
            return removed;
        }
    }

    public void SaveSettings() => _store.SaveSettings(Settings);
}
