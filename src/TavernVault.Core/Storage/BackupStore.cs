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
    /// 更换备份目录：两阶段迁移（v0.5.2 重写，修复 P1-3）。
    /// 阶段一为纯复制：逐个复制到新目录并校验长度，任一失败即清理本次已复制的产物并原样抛出，
    /// 期间不改变任何现有状态（_dir/_manifest 不动，旧目录完好，不会出现半写文件顶替原文件的记录）。
    /// 全部复制校验成功后才进入阶段二提交：切换目录、写新 manifest、再清理旧目录。
    /// 失败抛 IOException/UnauthorizedAccessException（上层 /api/settings/backup 已包装为 400）。
    /// </summary>
    public void RelocateTo(string newDir)
    {
        var target = Path.GetFullPath(newDir);
        lock (_lock)
        {
            if (string.Equals(Path.GetFullPath(_dir), target, StringComparison.OrdinalIgnoreCase))
                return;

            // ---- 阶段一：纯复制，不触碰现有状态 ----
            Directory.CreateDirectory(target);
            var copied = new List<(string Src, string Dst)>();
            try
            {
                foreach (var info in _manifest)
                {
                    var src = PathFor(info);
                    if (!File.Exists(src)) continue; // 源已不存在的记录：不迁移文件，随 manifest 保留
                    var dst = Path.Combine(target, Path.GetFileName(src));
                    copied.Add((src, dst)); // 先登记再复制：复制中途失败也能清掉半写产物
                    File.Copy(src, dst, overwrite: true);
                    if (new FileInfo(dst).Length != new FileInfo(src).Length)
                        throw new IOException($"迁移校验失败：{info.FileName} 复制后长度与源不一致");
                }
            }
            catch
            {
                // 回滚：只删除本次复制到新目录的产物，旧目录与 manifest 保持原状
                foreach (var (_, dst) in copied)
                    try { if (File.Exists(dst)) File.Delete(dst); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }

            // ---- 阶段二：提交 ----
            var oldDir = _dir;
            var oldManifest = _manifestPath;
            _dir = target;
            _manifestPath = Path.Combine(target, "manifest.json");
            Save(); // 先落新 manifest 再删旧文件：中断后旧目录仍是完整可用状态，manifest 与文件不错位

            // 逐个删除旧目录源文件：单个删除失败忽略（此时已双写，记录指向新目录）
            foreach (var (src, _) in copied)
                try { File.Delete(src); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            try { if (File.Exists(oldManifest)) File.Delete(oldManifest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>在覆盖写入前备份文件。失败时 error 带出异常消息，便于上层显性告警。</summary>
    public BackupInfo? BackupBeforeWrite(string fullPath, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(fullPath)) return null;
            lock (_lock)
            {
                // 目录可能被外部删除（清理工具/用户手删）：写前自愈，否则所有备份静默失败
                Directory.CreateDirectory(_dir);
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

    /// <summary>列出某原文件的全部备份（新→旧）。以磁盘为准：物理文件已不存在的记录不出现在结果里。</summary>
    public List<BackupInfo> List(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        lock (_lock)
            return _manifest.Where(b => string.Equals(b.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
                .Where(b => File.Exists(PathFor(b)))
                .OrderByDescending(b => b.SavedAt).ToList();
    }

    public BackupInfo? Find(string backupId)
    {
        lock (_lock) return _manifest.FirstOrDefault(b => b.Id == backupId);
    }

    /// <summary>还原备份。还原前会先备份当前文件（防还原错）；backupWarning 带出该备份的失败原因（null=正常）。</summary>
    public string? Restore(string backupId, out string? backupWarning)
    {
        backupWarning = null;
        lock (_lock)
        {
            var info = _manifest.FirstOrDefault(b => b.Id == backupId);
            var srcPath = info is null ? null : PathFor(info);
            if (info is null || !File.Exists(srcPath)) return null;

            // 先把源备份读入内存再触发轮转：还原前的安全备份会把本文件备份份数顶过上限，
            // PruneLocked 会删掉"最旧"的那份——恰好常是正在还原的条目，
            // 之后 File.Copy 就找不到源（v0.5.1 修复 N1）。
            byte[] snapshot;
            try { snapshot = File.ReadAllBytes(srcPath!); }
            catch (IOException) { return null; } // 存在性检查后被占用/消失，按"备份不存在"处理

            // 当前文件还在 → 先备份它，再还原
            if (File.Exists(info.OriginalPath) &&
                BackupBeforeWrite(info.OriginalPath, out var err) is null && err is not null)
                backupWarning = $"还原前备份当前文件失败（{err}）";

            // 原子写回：tmp + File.Replace，中断不产生半写的原文件
            var tmp = info.OriginalPath + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, snapshot);
                if (File.Exists(info.OriginalPath)) File.Replace(tmp, info.OriginalPath, null);
                else File.Move(tmp, info.OriginalPath);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(tmp, info.OriginalPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
                throw;
            }
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
        // v0.5.2 修复：与 List 一致按磁盘存在性过滤，幽灵条目不再计入统计
        lock (_lock)
        {
            var live = _manifest.Where(b => File.Exists(PathFor(b))).ToList();
            return (live.Count, live.Sum(b => b.SizeBytes));
        }
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

    /// <summary>v0.5.2 修复 P1-2：启动加载后缺席记录数 &gt; 0 时的告警文本；null 表示全部记录的文件都在磁盘上。</summary>
    public string? LoadWarning { get; private set; }

    private void Load()
    {
        LoadWarning = null;
        try
        {
            if (File.Exists(_manifestPath) &&
                JsonSerializer.Deserialize<List<BackupInfo>>(File.ReadAllText(_manifestPath), JsonOpts) is { } list)
            {
                // v0.5.2 修复 P1-2：不再按 File.Exists 过滤丢弃记录——备份目录瞬时不可见（移动盘未挂载、
                // 目录被临时改名等）时，过滤后的空表会在下一次任意写操作时把全部记录从盘上抹掉。
                // 缺席记录（幽灵条目）保留在内存即可：List/Restore/Stats 都按磁盘过滤/校验，无害且不丢记录。
                _manifest = list;
                var missing = list.Count(b => !File.Exists(PathFor(b)));
                if (missing > 0)
                    LoadWarning = $"备份目录有 {missing} 条备份记录的文件当前不可见（目录可能被移动、未挂载或文件被外部删除），" +
                        "相关备份暂不可用；记录已保留，目录恢复后自动可见";
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    private void Save()
    {
        try
        {
            // 与 SaveIndex/SaveSettings 一致的原子写：崩溃窗口不再留下截断的 manifest
            Directory.CreateDirectory(_dir);
            var tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_manifest, JsonOpts));
            File.Move(tmp, _manifestPath, overwrite: true);
        }
        catch (IOException) { }
    }
}
