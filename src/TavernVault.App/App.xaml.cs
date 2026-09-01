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

        // 单实例防护：双开会形成两个内存 Vault 共享同一 index.json/settings.json，互相覆盖丢更新
        _singleInstance = new Mutex(true, @"Local\TavernVault.SingleInstance", out var createdNew);
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
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
