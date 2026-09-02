using System.ComponentModel;
using System.Runtime.InteropServices;
using TavernVault.Core.Models;

namespace TavernVault.Core.FileOps;

/// <summary>文件操作：重命名 / 移动 / 回收站删除 / 在资源管理器中显示。全部限制在库目录内。</summary>
public static class FileOperations
{
    /// <summary>校验路径位于某个库根之下，防止越权访问。</summary>
    public static string GuardUnderRoots(string path, IEnumerable<string> roots)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in roots)
        {
            var rootFull = Path.GetFullPath(root);
            if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) return full;
            if (full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return full;
        }
        throw new UnauthorizedAccessException($"路径不在库目录内：{path}");
    }

    public static string Rename(LibraryItem item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("新名称不能为空");
        if (newName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("名称包含非法字符");

        var dir = Path.GetDirectoryName(item.FullPath)!;
        var target = Path.Combine(dir, newName + Path.GetExtension(item.FullPath));
        if (File.Exists(target)) throw new IOException("同名文件已存在");
        File.Move(item.FullPath, target);
        return target;
    }

    /// <summary>
    /// 把任意文本（如卡片内嵌的 name 字段——不可信内容）清洗为安全的单段文件名：
    /// 去除路径分隔符与系统保留字符、去除结尾的点/空格；结果只含一个路径段，绝不逃逸所在目录。
    /// 清洗后为空（空串/纯点）返回空串，由调用方回退默认名。
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned is "." or ".." ? "" : cleaned;
    }

    /// <summary>
    /// 为"另存为"生成同目录下的自动命名路径："{原名}-副本 yyyy-MM-dd_HHmmss{扩展名}"，
    /// 重名时追加序号。stem 会被 SanitizeFileName 清洗，杜绝经此路径写出库根。
    /// </summary>
    public static string GetSaveAsPath(string originalPath)
    {
        var dir = Path.GetDirectoryName(originalPath) ?? "";
        var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(originalPath));
        if (stem.Length == 0) stem = "未命名";
        var ext = Path.GetExtension(originalPath);
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var candidate = Path.Combine(dir, $"{stem}-副本 {ts}{ext}");
        int n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(dir, $"{stem}-副本 {ts} ({n++}){ext}");
        return candidate;
    }

    /// <summary>移动到某个库根下的相对目录（自动建目录）。</summary>
    public static string Move(LibraryItem item, string targetRoot, string relativeDir)
    {
        var root = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        relativeDir = (relativeDir ?? "").Trim('\\', '/');
        var targetDir = string.IsNullOrEmpty(relativeDir) ? root : Path.Combine(root, relativeDir);

        // 相对目录不得逃出根目录
        GuardUnderRoots(targetDir, [root]);
        Directory.CreateDirectory(targetDir);

        var target = Path.Combine(targetDir, item.FileName);
        if (target.Equals(item.FullPath, StringComparison.OrdinalIgnoreCase)) return target;
        if (File.Exists(target)) throw new IOException("目标已存在同名文件");
        File.Move(item.FullPath, target);
        return target;
    }

    /// <summary>删除到回收站。</summary>
    public static void Recycle(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("回收站删除仅支持 Windows");
        var from = path + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0) throw new Win32Exception($"删除失败（错误码 {rc}）");
    }

    public static void RevealInExplorer(string path)
    {
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // ---- Win32 回收站删除 ----
    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_SILENT = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
