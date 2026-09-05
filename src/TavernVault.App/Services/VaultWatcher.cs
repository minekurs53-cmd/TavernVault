using System.IO;
using TavernVault.App.Hosting;
using TavernVault.Core.Storage;

namespace TavernVault.App.Services;

/// <summary>
/// 库根文件监视（v0.7.2）：外部/酒馆侧改动自动重扫，免去手动点「重新扫描」。
/// 每个登记库根一个 FileSystemWatcher（含子目录），事件只做防抖（合并突发），
/// 到期后走一次增量 Rescan——未变化条目毫秒级复用，全量遍历代价可忽略。
/// 监视是纯读行为，不写库目录，不会与自身保存形成事件回环；数据目录（索引/备份）不在库根内，同样无回环。
/// </summary>
public sealed class VaultWatcher : IDisposable
{
    private const int DebounceMs = 800;
    private readonly Vault _vault;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Threading.Timer _debounce;
    private int _rescanning; // 0/1：重扫进行中时事件再触发只顺延，不并发重扫
    private bool _enabled;

    public VaultWatcher(Vault vault)
    {
        _vault = vault;
        _debounce = new System.Threading.Timer(_ => Drain(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>按当前设置启用监视（设置关闭时为空操作）。</summary>
    public void Start()
    {
        _enabled = _vault.Settings.AutoWatch;
        if (_enabled) Attach();
    }

    /// <summary>库根增删后重建监视器（/api/roots 变更时调用）。</summary>
    public void RefreshRoots()
    {
        Detach();
        if (_enabled) Attach();
    }

    private void Attach()
    {
        foreach (var root in _vault.Settings.LibraryRoots)
        {
            if (!Directory.Exists(root.Path)) continue;
            var fsw = new FileSystemWatcher(root.Path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024, // 大库突发变更防缓冲区溢出（溢出走 Error 重建兜底）
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            fsw.Created += OnEvent;
            fsw.Changed += OnEvent;
            fsw.Deleted += OnEvent;
            fsw.Renamed += OnEvent;
            fsw.Error += OnError;
            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
        }
    }

    private void Detach()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
        => _debounce.Change(DebounceMs, Timeout.Infinite); // 每个事件都把触发点后推，合并突发为一次重扫

    private void OnError(object sender, ErrorEventArgs e)
    {
        AppLog.Error("库根监视出错（缓冲区溢出/根目录不可达），已重建监视器", e.GetException());
        try { Detach(); Attach(); } catch { }
    }

    private void Drain()
    {
        if (Interlocked.Exchange(ref _rescanning, 1) == 1)
        {
            _debounce.Change(DebounceMs, Timeout.Infinite); // 上一轮未完：把触发点再推一轮，不丢事件
            return;
        }
        Task.Run(() =>
        {
            try
            {
                var n = _vault.Rescan();
                AppLog.Info($"自动重扫完成（文件监视触发）：{n} 个条目");
            }
            catch (Exception ex)
            {
                AppLog.Error("自动重扫失败", ex);
            }
            finally
            {
                Volatile.Write(ref _rescanning, 0);
            }
        });
    }

    public void Dispose()
    {
        _debounce.Dispose();
        Detach();
    }
}
