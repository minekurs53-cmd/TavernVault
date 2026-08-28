using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace TavernVault.App;

public partial class MainWindow : Window
{
    private readonly string _url;

    public MainWindow(string url)
    {
        InitializeComponent();
        _url = url;
        Loaded += async (_, _) =>
        {
            try
            {
                await Web.EnsureCoreWebView2Async();
                var settings = Web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                Web.CoreWebView2.NewWindowRequested += (s, e2) =>
                {
                    // 一律用系统浏览器打开外部链接
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e2.Uri) { UseShellExecute = true });
                    e2.Handled = true;
                };
                Web.CoreWebView2.Navigate(_url);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show("未找到 Microsoft Edge WebView2 运行时，请安装后重试。",
                    "缺少运行时", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        };
    }
}
