using System.Windows;

namespace TavernVault.App.Services;

/// <summary>在独立 STA 线程上弹出 WPF 系统文件夹选择框。</summary>
public static class FolderPicker
{
    public static string? Pick()
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择要纳入管理的资源文件夹",
                };
                if (dialog.ShowDialog() == true)
                    result = dialog.FolderName;
            }
            catch { /* 弹窗失败按取消处理 */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}
