using TavernVault.Core.FileOps;
using TavernVault.Core.Storage;

namespace TavernVault.Core.Tests;

public class BackupAndSaveAsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public BackupAndSaveAsTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    // ---------- 备份存储 ----------

    [Fact]
    public void Backup_Restore_Roundtrip()
    {
        var store = new BackupStore(_dir + "-store") { MaxPerFile = 5 };
        var file = WriteFile("书.json", "{\"v\":1}");

        var b1 = store.BackupBeforeWrite(file);
        Assert.NotNull(b1);

        File.WriteAllText(file, "{\"v\":2}"); // 模拟编辑
        var restored = store.Restore(b1!.Id);
        Assert.Equal(file, restored);
        Assert.Equal("{\"v\":1}", File.ReadAllText(file));

        // 还原前会先把当前内容备份一份
        Assert.Equal(2, store.List(file).Count);
    }

    [Fact]
    public void Backup_Prune_Keeps_Max_Per_File()
    {
        var store = new BackupStore(_dir + "-store") { MaxPerFile = 3 };
        var file = WriteFile("卡.json", "x");

        for (int i = 0; i < 6; i++)
        {
            store.BackupBeforeWrite(file);
            Thread.Sleep(15); // 时间戳可区分
            File.WriteAllText(file, "v" + i);
        }

        Assert.Equal(3, store.List(file).Count);
        Assert.Equal(3, store.Stats().count);
    }

    [Fact]
    public void BackupStore_Relocate_Moves_Files_And_Manifest()
    {
        var store = new BackupStore(Path.Combine(_dir, "data")) { MaxPerFile = 5 };
        var oldDir = store.Dir; // 默认 = data\backups
        var newDir = Path.Combine(_dir, "backups-elsewhere");
        var file = WriteFile("迁.json", "a");

        var b = store.BackupBeforeWrite(file);
        Assert.NotNull(b);
        Assert.True(Directory.GetFiles(oldDir).Length >= 2); // manifest + 备份文件

        store.RelocateTo(newDir);

        Assert.Equal(newDir, store.Dir);
        Assert.Single(store.List(file)); // 清单跟着走
        Assert.True(File.Exists(Path.Combine(newDir, "manifest.json")));
        // 旧目录搬空后删除（或已不存在）
        Assert.False(Directory.Exists(oldDir) && Directory.EnumerateFileSystemEntries(oldDir).Any());
    }

    [Fact]
    public void Vault_SetBackupRoot_Persists_And_Reloads()
    {
        var dataDir = Path.Combine(_dir, "data");
        var vault = new Vault(new SettingsStore(dataDir));
        var file = WriteFile("存.json", "b");
        vault.BackupBeforeWrite(file);

        var custom = Path.Combine(_dir, "custom-backups");
        vault.SetBackupRoot(custom);
        Assert.Equal(Path.GetFullPath(custom), vault.Backups.Dir);
        Assert.True(File.Exists(Path.Combine(custom, "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(dataDir, "backups"))); // 旧默认目录搬空后删除
        // 设置持久化：重建 Vault 后仍指向自定义位置
        Assert.Equal(Path.GetFullPath(custom), new Vault(new SettingsStore(dataDir)).Backups.Dir);

        // 恢复默认位置
        vault.SetBackupRoot(null);
        Assert.Equal(Path.Combine(dataDir, "backups"), vault.Backups.Dir);
        Assert.Null(new Vault(new SettingsStore(dataDir)).Settings.BackupRootPath);
    }

    [Fact]
    public void Backup_Delete_And_Missing_File()
    {
        var store = new BackupStore(_dir + "-store");
        var file = WriteFile("a.json", "data");
        var b = store.BackupBeforeWrite(file)!;

        Assert.True(store.Delete(b.Id));
        Assert.False(store.Delete(b.Id));          // 已删
        Assert.Null(store.BackupBeforeWrite(Path.Combine(_dir, "不存在.json"))); // 原文件缺失 → null
        Assert.Null(store.Restore("deadbeef"));    // 未知 id
    }

    // ---------- 另存为自动命名 ----------

    [Fact]
    public void SaveAsPath_Auto_Naming()
    {
        var file = WriteFile("椿.png", "png");
        var p1 = FileOperations.GetSaveAsPath(file);

        // 结构：前缀 + 副本 + 时间戳 + 扩展名，且不与原文件重名
        Assert.StartsWith("椿-副本 ", Path.GetFileName(p1));
        Assert.EndsWith(".png", Path.GetFileName(p1));
        Assert.NotEqual(file, p1);
        Assert.False(File.Exists(p1));

        // 占位后重名 → 追加序号
        File.WriteAllText(p1, "x");
        var p2 = FileOperations.GetSaveAsPath(file);
        Assert.Contains("(2)", Path.GetFileName(p2));
        Assert.NotEqual(p1, p2);
    }
}
