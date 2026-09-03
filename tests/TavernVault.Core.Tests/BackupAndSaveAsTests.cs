using TavernVault.Core.FileOps;
using TavernVault.Core.Scanning;
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

        var b1 = store.BackupBeforeWrite(file, out _);
        Assert.NotNull(b1);

        File.WriteAllText(file, "{\"v\":2}"); // 模拟编辑
        var restored = store.Restore(b1!.Id, out _);
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
            store.BackupBeforeWrite(file, out _);
            Thread.Sleep(15); // 时间戳可区分
            File.WriteAllText(file, "v" + i);
        }

        Assert.Equal(3, store.List(file).Count);
        Assert.Equal(3, store.Stats().count);
    }

    [Fact]
    public void Restore_Oldest_AtRetentionCap_Succeeds()
    {
        // N1 回归：满上限时还原"最旧"备份——还原前的安全备份会触发轮转并删除该条目，
        // 修复前 File.Copy 找不到源而必然失败
        var store = new BackupStore(_dir + "-cap") { MaxPerFile = 3 };
        var file = WriteFile("轮转.json", "v0");
        var ids = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            ids.Add(store.BackupBeforeWrite(file, out _)!.Id);
            File.WriteAllText(file, "v" + (i + 1));
            Thread.Sleep(15);
        }
        Assert.Equal(3, store.List(file).Count); // v0 已被轮转删除，最旧留存 = v1

        var restored = store.Restore(ids[1], out _);
        Assert.Equal(file, restored);
        Assert.Equal("v1", File.ReadAllText(file));
        Assert.Equal(3, store.List(file).Count); // 还原动作自身产生的安全备份同样受轮转约束
    }

    [Fact]
    public void SanitizeFileName_Blocks_Path_Escape()
    {
        Assert.Equal("C__Users_evil", FileOperations.SanitizeFileName(@"C:\Users\evil"));
        Assert.Equal("", FileOperations.SanitizeFileName(".."));
        Assert.Equal("", FileOperations.SanitizeFileName("   "));
        Assert.Equal("卡", FileOperations.SanitizeFileName("卡"));
        var tricky = FileOperations.SanitizeFileName(@"..\..\a/b");
        Assert.DoesNotContain('\\', tricky);
        Assert.DoesNotContain('/', tricky);

        // GetSaveAsPath 的产物必须仍落在原文件目录内（stem 可能来自不可信卡片 name）
        var path = FileOperations.GetSaveAsPath(@"C:\lib\sub\evil.json");
        Assert.Equal(@"C:\lib\sub", Path.GetDirectoryName(path));
    }

    [Fact]
    public void BackupStore_Relocate_Moves_Files_And_Manifest()
    {
        var store = new BackupStore(Path.Combine(_dir, "data")) { MaxPerFile = 5 };
        var oldDir = store.Dir; // 默认 = data\backups
        var newDir = Path.Combine(_dir, "backups-elsewhere");
        var file = WriteFile("迁.json", "a");

        var b = store.BackupBeforeWrite(file, out _);
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
        var b = store.BackupBeforeWrite(file, out _)!;

        Assert.True(store.Delete(b.Id));
        Assert.False(store.Delete(b.Id));          // 已删
        Assert.Null(store.BackupBeforeWrite(Path.Combine(_dir, "不存在.json"), out _)); // 原文件缺失 → null
        Assert.Null(store.Restore("deadbeef", out _));    // 未知 id
    }

    [Fact]
    public void Vault_BackupBeforeWrite_Warns_When_Backup_Fails()
    {
        var dataDir = Path.Combine(_dir, "data");
        var vault = new Vault(new SettingsStore(dataDir));
        var file = WriteFile("警.json", "v1");

        // 正常路径：备份成功，无警告
        Assert.Null(vault.BackupBeforeWrite(file));
        Assert.Single(vault.Backups.List(file));

        // 备份目录被同名文件占死 → 写入必失败，外显警告而非静默
        var backupsDir = vault.Backups.Dir;
        Directory.Delete(backupsDir, recursive: true);
        File.WriteAllText(backupsDir, "occupied");

        var warning = vault.BackupBeforeWrite(file);
        Assert.NotNull(warning);
        Assert.Contains("自动备份失败", warning!);
    }

    [Fact]
    public void Vault_UpsertItem_PreservesUserData_And_Removes()
    {
        // 增量更新不得丢用户数据（收藏/标签），且要同步 _byId 索引
        var dataDir = Path.Combine(_dir, "data2");
        var rootDir = Path.Combine(_dir, "root");
        Directory.CreateDirectory(rootDir);
        var file = Path.Combine(rootDir, "条目.json");
        File.WriteAllText(file, """{"name":"条目","content":"x"}""");

        var vault = new Vault(new SettingsStore(dataDir));
        vault.AddRoot(rootDir);
        vault.Rescan();
        var id = LibraryScanner.ComputeId(file);
        Assert.NotNull(vault.Find(id));

        Assert.True(vault.SetFavorite(id, true));
        Assert.True(vault.SetUserTags(id, ["我的标签"]));

        // 模拟编辑后落盘：内容变化（mtime/size 变化），增量更新
        File.WriteAllText(file, """{"name":"条目","content":"编辑后更长内容"}""");
        var updated = vault.UpsertItem(file);
        Assert.NotNull(updated);
        Assert.True(updated!.Favorite);
        Assert.Contains("我的标签", updated.UserTags);
        Assert.NotNull(vault.Find(id)); // Find 字典同步
        Assert.Single(vault.Items);

        // 删除路径移除条目
        Assert.True(vault.RemoveItem(file));
        Assert.Null(vault.Find(id));
        Assert.Empty(vault.Items);
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
