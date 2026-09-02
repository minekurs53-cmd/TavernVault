using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using TavernVault.App.Hosting;

namespace TavernVault.App;

public partial class App : Application
{
    private WebApplication? _server;
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI 线程未处理异常", args.Exception);
            MessageBox.Show(args.Exception.Message, "酒馆资源管家 · 未处理异常",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        // 单实例防护：双开会形成两个内存 Vault 共享同一 index.json/settings.json，互相覆盖丢更新。
        // Mutex 按数据目录哈希命名（与 ApiServer.ResolveDataDir 同源）：
        // 不同数据目录（如窗口模式 + --server 冒烟）可以并存，同一目录仍然互斥（v0.5.1）。
        var dataDir = Path.GetFullPath(ApiServer.ResolveDataDir(e.Args)).TrimEnd('\\').ToLowerInvariant();
        var mutexKey = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir)))[..12];
        _singleInstance = new Mutex(true, @"Local\TavernVault." + mutexKey, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("酒馆资源管家已在运行（同一数据目录只允许一个实例）。",
                "酒馆资源管家", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            var handle = ApiServer.Build(e.Args);
            _server = handle.App;
            await _server.StartAsync();
            var url = _server.Urls.First();
            AppLog.Info($"服务已启动 {url}");

            if (e.Args.Contains("--server"))
            {
                // 无窗口模式（WinExe 无控制台）：连接信息落盘，便于脚本读取
                Console.WriteLine($"TavernVault listening at {url}");
                try
                {
                    var connPath = Path.Combine(handle.DataDir, "server-connection.json");
                    File.WriteAllText(connPath, JsonSerializer.Serialize(
                        new { url, token = handle.Token },
                        new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"Connection file: {connPath}");
                }
                catch (IOException) { }
            }
            else
            {
                var win = new MainWindow(url, handle.Token);
                MainWindow = win;
                win.Show();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("启动失败", ex);
            MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_server is not null)
            await _server.StopAsync();
        // 只有真正持有所有权的线程才能 Release，否则抛 ApplicationException；
        // 进程退出时 OS 会自动放弃未释放的 Mutex，这里释放只是提前归还
        if (_ownsMutex)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
