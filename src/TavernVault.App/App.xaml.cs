using System.Windows;
using Microsoft.AspNetCore.Builder;
using TavernVault.App.Hosting;

namespace TavernVault.App;

public partial class App : Application
{
    private WebApplication? _server;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "酒馆资源管家 · 未处理异常",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        try
        {
            _server = ApiServer.Build(e.Args);
            await _server.StartAsync();
            var url = _server.Urls.First();

            if (e.Args.Contains("--server"))
            {
                // 无窗口模式：供调试 / 远程访问
                Console.WriteLine($"TavernVault listening at {url}");
            }
            else
            {
                var win = new MainWindow(url);
                MainWindow = win;
                win.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_server is not null)
            await _server.StopAsync();
        base.OnExit(e);
    }
}
