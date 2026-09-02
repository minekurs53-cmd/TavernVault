using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TavernVault.Core.Models;

namespace TavernVault.App.Services;

/// <summary>
/// 角色卡 PNG 缩略图：WPF 解码缩放后缓存为 JPEG。
/// 缓存位于 {数据目录}\thumbs，按源文件 mtime+size 旁车记录失效。
/// </summary>
public sealed class ThumbnailService
{
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public ThumbnailService(string dataDir)
    {
        // 随数据目录走（v0.5.1 起）：避免 --data 测试模式污染真实 %APPDATA%、测试与生产缓存串台
        _dir = Path.Combine(dataDir, "thumbs");
        Directory.CreateDirectory(_dir);
    }

    public async Task<string?> GetAsync(LibraryItem item)
    {
        if (!item.HasEmbeddedCard) return null;
        if (!File.Exists(item.FullPath)) return null;

        var cache = Path.Combine(_dir, item.Id + ".jpg");
        var meta = cache + ".meta";
        var key = $"{item.ModifiedAt.Ticks}|{item.SizeBytes}";
        // 失效键比对源文件特征（记录于旁车文件）而非缓存文件 mtime：
        // 还原旧备份后源 mtime 回退，按旧规则陈旧缓存会被误判新鲜（v0.5.1 修复）
        if (File.Exists(cache) && ReadAllTextOrNull(meta) == key)
            return cache;

        await _gate.WaitAsync();
        try
        {
            if (File.Exists(cache) && ReadAllTextOrNull(meta) == key)
                return cache;

            // 目录可能被外部清理（如冒烟脚本）：写前自愈
            Directory.CreateDirectory(_dir);
            var tmp = cache + ".tmp";
            await Task.Run(() => Build(item.FullPath, tmp));
            if (!File.Exists(tmp)) return null;
            File.Move(tmp, cache, overwrite: true);
            File.WriteAllText(meta, key);
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

    private static string? ReadAllTextOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
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
            catch
            {
                // 坏图放弃；残缺 tmp 必须删除，否则会被上层误当作成功结果写入缓存
                try { File.Delete(dst); } catch (IOException) { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
