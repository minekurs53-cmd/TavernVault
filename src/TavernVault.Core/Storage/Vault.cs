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
    public string? RootPath { get; set; }
    /// <summary>按逻辑库来源过滤（局外/酒馆并集）。null = 不过滤。与 RootPath 同设为 AND。</summary>
    public LibrarySource? Source { get; set; }
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
    private Dictionary<string, LibraryItem> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>文件级备份（编辑/还原前自动备份）。</summary>
    public BackupStore Backups { get; }

    public AppSettings Settings { get; private set; }
    public DateTime LastScanAt { get; private set; }
    public string DataDir => _store.DataDir;

    public Vault(SettingsStore? store = null)
    {
        _store = store ?? new SettingsStore();
        Settings = _store.LoadSettings();
        Items = _store.LoadIndex();
        RebuildById();
        Backups = new BackupStore(_store.DataDir, Settings.BackupRootPath) { MaxPerFile = Settings.MaxBackupsPerFile };
        Backups.RetentionFor = path => RetentionFor(RootContaining(path));
        // 来源一致性自愈：--server 冷启动与手改索引不会自动重扫，
        // 条目缺 RootSource 字段会反序列化为 Normal、根来源被改后旧条目也不会自动跟进。
        // 发现漂移立即重扫一次（增量复用分支会无条件按当前根刷新 RootSource）。
        if (Items.Any(i => RootContaining(i.FullPath)?.Source != i.RootSource))
            Rescan();
    }

    /// <summary>
    /// 更换备份目录（null/空 = 恢复数据目录默认位置）。现有备份会被移动过去。
    /// </summary>
    public string SetBackupRoot(string? path)
    {
        lock (_lock)
        {
            var dir = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
            Settings.BackupRootPath = dir;
            Backups.RelocateTo(dir ?? Path.Combine(_store.DataDir, "backups"));
            _store.SaveSettings(Settings);
            return Backups.Dir;
        }
    }

    private int RetentionFor(LibraryRoot? root)
        => root?.Source == LibrarySource.TavernTT ? 10 : Math.Max(1, Settings.MaxBackupsPerFile);

    /// <summary>
    /// 若开启自动备份或文件属于酒馆来源，在覆盖写入前调用。
    /// 返回 null 表示备份正常；返回警告文本表示本次保存无备份兜底（备份失败，调用方应显性告警）。
    /// </summary>
    public string? BackupBeforeWrite(string fullPath)
    {
        var root = RootContaining(fullPath);
        var isTavern = root is not null && root.Source != LibrarySource.Normal;
        if (!Settings.AutoBackup && !isTavern) return null;
        if (!File.Exists(fullPath)) return null;
        Backups.MaxPerFile = RetentionFor(root);
        return Backups.BackupBeforeWrite(fullPath, out var error) is null
            ? $"自动备份失败（{error}）：本次保存无备份兜底"
            : null;
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
            RebuildById();
            LastScanAt = DateTime.Now;
            _store.SaveIndex(Items);
        }
        return items.Count;
    }

    public LibraryItem? Find(string id)
    {
        lock (_lock) return _byId.GetValueOrDefault(id);
    }

    /// <summary>
    /// 增量更新单文件条目（编辑/另存为/还原后调用），避免全量 Rescan 的 O(库文件数) 目录枚举。
    /// 原条目的收藏/标签会保留到重建后的条目上。
    /// </summary>
    public LibraryItem? UpsertItem(string fullPath)
    {
        lock (_lock)
        {
            var full = Path.GetFullPath(fullPath);
            var newId = LibraryScanner.ComputeId(full);

            // 捕获旧条目（同 Id 或同路径）的用户数据，重建后回填
            var old = _byId.GetValueOrDefault(newId)
                ?? Items.FirstOrDefault(i => string.Equals(i.FullPath, full, StringComparison.OrdinalIgnoreCase));
            var (fav, tags) = old is null ? (false, new List<string>()) : (old.Favorite, new List<string>(old.UserTags));

            // 移除旧同 Id 条目与旧同路径条目（重命名后旧 Id 残留的情况）
            Items.RemoveAll(i => i.Id == newId || string.Equals(i.FullPath, full, StringComparison.OrdinalIgnoreCase));
            _byId.Remove(newId);

            var root = RootContaining(full);
            if (root is null) { _store.SaveIndex(Items); return null; }

            var item = LibraryScanner.BuildItem(full, root.Path, root.Source,
                new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase), _byId);
            if (item is null) { _store.SaveIndex(Items); return null; }

            item.Favorite = fav;
            item.UserTags = tags;
            Items.Add(item);
            _byId[item.Id] = item;
            _store.SaveIndex(Items);
            return item;
        }
    }

    /// <summary>按文件路径移除条目（删除/重命名/移动前调用）。</summary>
    public bool RemoveItem(string fullPath)
    {
        lock (_lock)
        {
            var full = Path.GetFullPath(fullPath);
            var idx = Items.FindIndex(i => string.Equals(i.FullPath, full, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _byId.Remove(Items[idx].Id);
            Items.RemoveAt(idx);
            _store.SaveIndex(Items);
            return true;
        }
    }

    private void RebuildById()
    {
        _byId = new Dictionary<string, LibraryItem>(Items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var i in Items) _byId[i.Id] = i;
    }

    public List<LibraryItem> Query(QueryParams p)
    {
        lock (_lock)
        {
            IEnumerable<LibraryItem> q = Items;

            if (p.Kind is { } kind) q = q.Where(i => i.Kind == kind);
            if (p.Favorite is { } fav) q = q.Where(i => i.Favorite == fav);
            if (p.Source is { } src) q = q.Where(i => i.RootSource == src);
            if (!string.IsNullOrEmpty(p.RootPath))
                q = q.Where(i => string.Equals(i.RootPath, p.RootPath, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// 聚合三个逻辑库（按库根来源并集：局外存储/SillyTavern/TauriTavern）的计数。
    /// 酒馆库目录按注册根逐条列出（含空根），普通库目录按相对路径跨根聚合。
    /// </summary>
    public List<LibraryInfo> BuildLibraries()
    {
        lock (_lock)
        {
            var result = new List<LibraryInfo>();
            foreach (var (src, key, label) in new[]
            {
                (LibrarySource.Normal, "normal", "局外存储"),
                (LibrarySource.TavernST, "tavernST", "SillyTavern"),
                (LibrarySource.TavernTT, "tavernTT", "TauriTavern"),
            })
            {
                var items = Items.Where(i => i.RootSource == src).ToList();
                var lib = new LibraryInfo
                {
                    Key = key,
                    Label = label,
                    Total = items.Count,
                    RootCount = Settings.LibraryRoots.Count(r => r.Source == src),
                    Favorites = items.Count(i => i.Favorite),
                    Kinds = [.. ItemKindText.All.Select(a => new KindCount
                    {
                        Kind = a.Key,
                        Label = a.Label,
                        Count = items.Count(i => i.Kind == a.Kind),
                    })],
                    Tags = [.. items.SelectMany(i => i.UserTags)
                        .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count())
                        .Select(g => new TagCount { Tag = g.Key, Count = g.Count() })],
                };
                if (src == LibrarySource.Normal)
                {
                    lib.Dirs = [.. items
                        .GroupBy(i => i.RelativeDir, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new DirCount { Root = null, Dir = g.Key, Count = g.Count() })
                        .OrderByDescending(d => d.Count)
                        .ThenBy(d => d.Dir, StringComparer.OrdinalIgnoreCase)];
                }
                else
                {
                    lib.Dirs = [.. Settings.LibraryRoots.Where(r => r.Source == src)
                        .Select(r => new DirCount
                        {
                            Root = r.Path,
                            Dir = "",
                            Count = items.Count(i => string.Equals(i.RootPath, r.Path, StringComparison.OrdinalIgnoreCase)),
                        })];
                }
                result.Add(lib);
            }
            return result;
        }
    }

    public bool SetFavorite(string id, bool fav)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var item)) return false;
            item.Favorite = fav;
            _store.SaveIndex(Items);
            return true;
        }
    }

    public bool SetUserTags(string id, List<string> tags)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var item)) return false;
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
            return _byId.TryGetValue(id, out var item)
                ? (item.Favorite, [.. item.UserTags])
                : (false, []);
        }
    }

    /// <summary>把快照应用到新条目（重命名/移动后 Id 变化时迁移用户数据）。</summary>
    public bool SetUserData(string id, bool favorite, List<string> tags)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var item)) return false;
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
