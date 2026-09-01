using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TavernVault.Core.Storage;

public class BackupInfo
{
    public string Id { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>
/// 文件级备份存储：%DATA%\backups 下按 "{时间戳}-{原文件名}" 保存副本，
/// manifest.json 记录元数据。每个原文件最多保留 MaxPerFile 份（超出删最旧）。
/// 备份失败绝不阻断主流程（返回 null）。
/// </summary>
public sealed class BackupStore
{
    private readonly object _lock = new();
    private string _dir;
    private string _manifestPath;
    private List<BackupInfo> _manifest = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public BackupStore(string dataDir, string? backupRootPath = null)
    {
        _dir = string.IsNullOrWhiteSpace(backupRootPath)
            ? Path.Combine(dataDir, "backups")
            : Path.GetFullPath(backupRootPath.Trim());
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "manifest.json");
        Load();
    }

    /// <summary>当前备份目录（供设置界面回显）。</summary>
    public string Dir => _dir;

    /// <summary>
    /// 更换备份目录：移动现有备份文件与 manifest 到新目录。
    /// 旧目录在搬空后尝试删除。抛 IOException/UnauthorizedAccessException 时不改变现有状态。
    /// </summary>
    public void RelocateTo(string newDir)
    {
        var target = Path.GetFullPath(newDir);
        lock (_lock)
        {
            if (string.Equals(Path.GetFullPath(_dir), target, StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(target);

            var moved = new List<BackupInfo>();
            foreach (var info in _manifest.ToList())
            {
                var src = PathFor(info);
                var dst = Path.Combine(target, Path.GetFileName(src));
                try
                {
                    if (File.Exists(src)) File.Move(src, dst);
                    moved.Add(info);
                }
                catch (IOException) { moved.Add(info); } // 源文件丢了也保留记录
            }

            var oldManifest = _manifestPath;
            var oldDir = _dir;
            _manifest = moved;
            _dir = target;
            _manifestPath = Path.Combine(target, "manifest.json");
            Save();

            try { if (File.Exists(oldManifest)) File.Delete(oldManifest); } catch (IOException) { }
            try { if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir); } catch (IOException) { }
        }
    }

    /// <summary>在覆盖写入前备份文件。失败返回 null 且不抛出（旧签名，保留兼容）。</summary>
    public BackupInfo? BackupBeforeWrite(string fullPath) => BackupBeforeWrite(fullPath, out _);

    /// <summary>在覆盖写入前备份文件。失败时 error 带出异常消息，便于上层显性告警。</summary>
    public BackupInfo? BackupBeforeWrite(string fullPath, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(fullPath)) return null;
            lock (_lock)
            {
                var ts = DateTime.Now;
                var info = new BackupInfo
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    OriginalPath = Path.GetFullPath(fullPath),
                    FileName = Path.GetFileName(fullPath),
                    SavedAt = ts,
                    SizeBytes = new FileInfo(fullPath).Length,
                };
                var backupPath = PathFor(info);
                File.Copy(fullPath, backupPath, overwrite: true);
                _manifest.Add(info);
                PruneLocked(info.OriginalPath);
                Save();
                return info;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>列出某原文件的全部备份（新→旧）。</summary>
    public List<BackupInfo> List(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        lock (_lock)
            return _manifest.Where(b => string.Equals(b.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.SavedAt).ToList();
    }

    public BackupInfo? Find(string backupId)
    {
        lock (_lock) return _manifest.FirstOrDefault(b => b.Id == backupId);
    }

    /// <summary>还原备份。还原前会先备份当前文件（防还原错）。返回还原到的原路径。</summary>
    public string? Restore(string backupId) => Restore(backupId, out _);

    /// <summary>同上；backupWarning 带出还原前备份当前文件的失败原因（null=正常）。</summary>
    public string? Restore(string backupId, out string? backupWarning)
    {
        backupWarning = null;
        lock (_lock)
        {
            var info = _manifest.FirstOrDefault(b => b.Id == backupId);
            if (info is null || !File.Exists(PathFor(info))) return null;

            // 当前文件还在 → 先备份它，再还原
            if (File.Exists(info.OriginalPath) &&
                BackupBeforeWrite(info.OriginalPath, out var err) is null && err is not null)
                backupWarning = $"还原前备份当前文件失败（{err}）";

            File.Copy(PathFor(info), info.OriginalPath, overwrite: true);
            return info.OriginalPath;
        }
    }

    public bool Delete(string backupId)
    {
        lock (_lock)
        {
            var info = _manifest.FirstOrDefault(b => b.Id == backupId);
            if (info is null) return false;
            try { if (File.Exists(PathFor(info))) File.Delete(PathFor(info)); }
            catch (IOException) { }
            _manifest.Remove(info);
            Save();
            return true;
        }
    }

    public (int count, long bytes) Stats()
    {
        lock (_lock) return (_manifest.Count, _manifest.Sum(b => b.SizeBytes));
    }

    // ---- 内部 ----

    private string PathFor(BackupInfo info) =>
        Path.Combine(_dir, $"{info.Id}-{info.SavedAt:yyyyMMdd-HHmmss}-{Sanitize(info.FileName)}");

    private static string Sanitize(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    private void PruneLocked(string originalPath)
    {
        var max = RetentionFor?.Invoke(originalPath) ?? MaxPerFile;
        var mine = _manifest.Where(b => string.Equals(b.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.SavedAt).ToList();
        foreach (var old in mine.Skip(max))
        {
            try { if (File.Exists(PathFor(old))) File.Delete(PathFor(old)); }
            catch (IOException) { }
            _manifest.Remove(old);
        }
    }

    /// <summary>每文件保留份数（由设置写入，这里读环境默认 5）。</summary>
    public int MaxPerFile { get; set; } = 5;

    /// <summary>按原路径自定义保留份数（酒馆缓存目录可提高上限）。返回 null 则回退 MaxPerFile。</summary>
    public Func<string, int>? RetentionFor { get; set; }

    private void Load()
    {
        try
        {
            if (File.Exists(_manifestPath) &&
                JsonSerializer.Deserialize<List<BackupInfo>>(File.ReadAllText(_manifestPath), JsonOpts) is { } list)
                _manifest = list.Where(b => File.Exists(PathFor(b))).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(_manifest, JsonOpts));
        }
        catch (IOException) { }
    }
}
