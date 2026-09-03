using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace TavernVault.App;

public partial class MainWindow : Window
{
    private readonly string _url;
    private readonly string _token;

    public MainWindow(string url, string token)
    {
        InitializeComponent();
        _url = url;
        _token = token;
        Loaded += async (_, _) =>
        {
            try
            {
                // v0.5.2 修复 P2-12：显式指定用户数据目录（%LOCALAPPDATA%\TavernVault\WebView2）——
                // bin 目录不再被浏览器数据撑大，Program Files 只读场景也能正常启动
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TavernVault", "WebView2"));
                await Web.EnsureCoreWebView2Async(env);
                var settings = Web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                Web.CoreWebView2.NewWindowRequested += (s, e2) =>
                {
                    // v0.5.2 修复 P2-11：仅放行 http/https 外链（file:///、shell: 等一律不响应），
                    // 放行的链接用系统浏览器打开
                    if (e2.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        e2.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e2.Uri) { UseShellExecute = true });
                    e2.Handled = true;
                };
                // v0.5.2 修复 P2-11：导航仅限本机服务——令牌会注入"一切文档"，
                // 拦截外部导航防止令牌被注入外部页面后外泄
                Web.CoreWebView2.NavigationStarting += (s, e2) =>
                {
                    try
                    {
                        var h = new Uri(e2.Uri).Host;
                        if (h != "127.0.0.1" && h != "localhost") e2.Cancel = true;
                    }
                    catch (UriFormatException)
                    {
                        e2.Cancel = true; // 解析失败的 URI 一律不放行
                    }
                };
                // 先注入令牌再导航：首个文档任何脚本执行前 window.__TV_TOKEN__ 已就位（对后续导航同样生效）
                var literal = System.Text.Json.JsonSerializer.Serialize(_token);
                await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    $"window.__TV_TOKEN__ = {literal};");
                Web.CoreWebView2.Navigate(_url);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show("未找到 Microsoft Edge WebView2 运行时，请安装后重试。",
                    "缺少运行时", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
            catch (Exception ex)
            {
                // v0.5.2 修复 P2-12：初始化失败不再静默变成空白窗口，给出可操作提示
                MessageBox.Show(
                    $"WebView2 初始化失败：{ex.Message}\n\n请关闭程序后重试；若反复出现，可尝试删除 %LOCALAPPDATA%\\TavernVault\\WebView2 目录后再启动。",
                    "WebView2 初始化失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        };
    }
}
