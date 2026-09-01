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
            var path = Path.Combine(_dir, $"tavernvault-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            lock (_lock) File.AppendAllText(path, line);
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
