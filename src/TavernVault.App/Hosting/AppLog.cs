using System.IO;
using System.Text;

namespace TavernVault.App.Hosting;

/// <summary>
/// 数据目录下的滚动日志（logs/tavernvault-YYYYMMDD.log），保留 7 天。
/// 内部 IO 异常全吞——日志永不拖垮主流程。
/// </summary>
public static class AppLog
{
    private static string? _dir;
    private static string? _lastDay; // v0.5.2 修复 P2-13：上次写入的日期（yyyyMMdd），跨天时补一次过期清理
    private static readonly object _lock = new();

    public static void Init(string dataDir)
    {
        _dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(_dir);
        PruneOld();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex is not null)
        {
            sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            var frames = ex.StackTrace?.Split('\n', 4);
            if (frames is { Length: > 1 })
                for (int i = 1; i < Math.Min(frames.Length, 3); i++)
                    sb.Append(' ').Append(frames[i].Trim());
        }
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        if (_dir is null) return;
        try
        {
            var now = DateTime.Now;
            var day = now.ToString("yyyyMMdd");
            var path = Path.Combine(_dir, $"tavernvault-{day}.log");
            var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(path, line);
                // v0.5.2：日期切换（应用跨天长驻）时触发一次过期日志清理——此前仅在启动时清理一次
                var dayChanged = _lastDay is not null && _lastDay != day;
                _lastDay = day;
                if (dayChanged) PruneOld();
            }
        }
        catch { /* 日志永不抛出 */ }
    }

    private static void PruneOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(_dir!, "tavernvault-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
