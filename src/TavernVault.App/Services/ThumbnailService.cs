using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TavernVault.Core.Models;

namespace TavernVault.App.Services;

/// <summary>
/// 角色卡 PNG 缩略图：WPF 解码缩放后缓存为 JPEG。
/// 缓存位于 %APPDATA%\TavernVault\thumbs，按条目 Id + 修改时间失效。
/// </summary>
public sealed class ThumbnailService
{
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public ThumbnailService()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TavernVault", "thumbs");
        Directory.CreateDirectory(_dir);
    }

    public async Task<string?> GetAsync(LibraryItem item)
    {
        if (!item.HasEmbeddedCard) return null;
        if (!File.Exists(item.FullPath)) return null;

        var cache = Path.Combine(_dir, item.Id + ".jpg");
        if (File.Exists(cache) && File.GetLastWriteTime(cache) >= item.ModifiedAt)
            return cache;

        await _gate.WaitAsync();
        try
        {
            if (File.Exists(cache) && File.GetLastWriteTime(cache) >= item.ModifiedAt)
                return cache;

            var tmp = cache + ".tmp";
            await Task.Run(() => Build(item.FullPath, tmp));
            if (!File.Exists(tmp)) return null;
            File.Move(tmp, cache, overwrite: true);
            return cache;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Build(string src, string dst)
    {
        // WPF 图像类要求 STA 线程
        var thread = new Thread(() =>
        {
            try
            {
                using var fs = File.OpenRead(src);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.FirstOrDefault();
                if (frame is null) return;

                const int maxEdge = 448;
                double scale = Math.Min(1.0, maxEdge / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
                BitmapSource scaled = scale >= 1.0
                    ? frame
                    : new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                if (scaled.CanFreeze) scaled.Freeze();

                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(scaled));
                using var outFs = File.Create(dst);
                encoder.Save(outFs);
            }
            catch { /* 坏图直接放弃，返回无缩略图 */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
