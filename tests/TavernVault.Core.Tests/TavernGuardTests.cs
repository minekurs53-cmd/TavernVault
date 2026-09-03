using TavernVault.Core.Detection;
using TavernVault.Core.Models;
using TavernVault.Core.Storage;

namespace TavernVault.Core.Tests;

/// <summary>
/// 酒馆护栏与 v0.5.2 备份可靠性回归（审计缺口 T2 / P1-2 / P1-3）：
/// 强制备份、TT 保留份数、环境变量探测、RelocateTo 两阶段、Load 幽灵告警。
/// 全部不需要真实酒馆安装。
/// </summary>
public class TavernGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TavernGuardTests() => Directory.CreateDirectory(_dir);
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

    private LibraryRoot RootOf(string path, LibrarySource source) => new() { Path = path, Source = source };

    // ---------- 护栏：强制备份与保留份数 ----------

    [Fact]
    public void Vault_TavernSource_ForcedBackup_EvenWhenAutoBackupOff()
    {
        // T2 回归：酒馆来源无视 AutoBackup 开关照常备份——重构删掉 isTavern 判断必须在此变红
        var vault = new Vault(new SettingsStore(Path.Combine(_dir, "data")));
        vault.Settings.AutoBackup = false;

        // 酒馆来源根（TauriTavern）内的文件：关闭开关仍产生备份
        var ttRoot = Directory.CreateDirectory(Path.Combine(_dir, "tt-characters"));
        var ttFile = Path.Combine(ttRoot.FullName, "酒馆卡.json");
        File.WriteAllText(ttFile, """{"name":"酒馆卡"}""");
        vault.AddRoot(RootOf(ttRoot.FullName, LibrarySource.TavernTT));

        Assert.Null(vault.BackupBeforeWrite(ttFile)); // null = 备份成功且无警告
        Assert.Single(vault.Backups.List(ttFile));    // 护栏核心断言：开关关闭仍产生备份

        // 普通来源根：同样条件下不得备份
        var normalRoot = Directory.CreateDirectory(Path.Combine(_dir, "normal"));
        var normalFile = Path.Combine(normalRoot.FullName, "普通卡.json");
        File.WriteAllText(normalFile, """{"name":"普通卡"}""");
        vault.AddRoot(RootOf(normalRoot.FullName, LibrarySource.Normal));

        Assert.Null(vault.BackupBeforeWrite(normalFile));
        Assert.Empty(vault.Backups.List(normalFile));
    }

    [Fact]
    public void Vault_TavernTT_Retention_Ten_Normal_Follows_Setting()
    {
        var vault = new Vault(new SettingsStore(Path.Combine(_dir, "data")));
        vault.Settings.MaxBackupsPerFile = 2;

        var ttRoot = Directory.CreateDirectory(Path.Combine(_dir, "tt-worlds"));
        var ttFile = Path.Combine(ttRoot.FullName, "酒馆书.json");
        File.WriteAllText(ttFile, "v0");
        vault.AddRoot(RootOf(ttRoot.FullName, LibrarySource.TavernTT));

        var normalRoot = Directory.CreateDirectory(Path.Combine(_dir, "normal"));
        var normalFile = Path.Combine(normalRoot.FullName, "普通书.json");
        File.WriteAllText(normalFile, "v0");
        vault.AddRoot(normalRoot.FullName); // 普通来源（默认 Normal）

        // 同一存储内两个来源交替备份：TT 固定保留 10 份，普通源跟随设置 2 份
        for (var i = 0; i < 11; i++)
        {
            Assert.Null(vault.BackupBeforeWrite(ttFile));     // null = 备份成功且无警告
            Assert.Null(vault.BackupBeforeWrite(normalFile));
            File.WriteAllText(ttFile, "v" + i);
            File.WriteAllText(normalFile, "v" + i);
            Thread.Sleep(15); // SavedAt 可区分（与既有轮转测试同款）
        }

        Assert.Equal(10, vault.Backups.List(ttFile).Count);
        Assert.Equal(2, vault.Backups.List(normalFile).Count);
    }

    // ---------- TavernDetector：环境变量优先 + characters 校验 ----------

    [Fact]
    public void TavernDetector_EnvVar_Priority_And_Characters_Gate()
    {
        var original = Environment.GetEnvironmentVariable("TV_SILLYTAVERN_DATA");
        try
        {
            // 候选目录含 characters/ 子目录 → 采用环境变量指向
            var valid = Path.GetFullPath(Path.Combine(_dir, "st"));
            Directory.CreateDirectory(Path.Combine(valid, "characters"));
            Environment.SetEnvironmentVariable("TV_SILLYTAVERN_DATA", valid);

            var st = Assert.Single(TavernDetector.DetectAll(),
                d => d.source == LibrarySource.TavernST);
            Assert.Equal("SillyTavern", st.label);
            Assert.Equal(valid, st.baseDir);
            Assert.Contains("characters", st.subdirs);

            // 目录不含 characters/ 子目录 → 不被采用（若本机恰有约定路径的真实安装，
            // tavernST 仍会经回退出现，但绝不指向这个残缺目录）
            var invalid = Directory.CreateDirectory(Path.Combine(_dir, "st-empty")).FullName;
            Environment.SetEnvironmentVariable("TV_SILLYTAVERN_DATA", invalid);
            Assert.DoesNotContain(TavernDetector.DetectAll(),
                d => d.source == LibrarySource.TavernST && d.baseDir == invalid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TV_SILLYTAVERN_DATA", original);
        }
    }

    // ---------- BackupStore.RelocateTo 两阶段（v0.5.2 P1-3） ----------

    [Fact]
    public void BackupStore_RelocateTo_Failure_KeepsOldState()
    {
        var store = new BackupStore(Path.Combine(_dir, "data")) { MaxPerFile = 5 };
        var oldDir = store.Dir;
        var f1 = WriteFile("一.json", "1");
        var f2 = WriteFile("二.json", "2");
        Assert.NotNull(store.BackupBeforeWrite(f1, out _));
        Assert.NotNull(store.BackupBeforeWrite(f2, out _));

        // 坏目标：路径已被同名文件占用 → Directory.CreateDirectory 抛出，
        // 两阶段设计的失败必须原样抛出且旧目录、manifest、记录分毫不动
        var occupied = WriteFile("占位文件", "x");
        var ex = Record.Exception(() => store.RelocateTo(occupied));
        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException, $"实际抛出 {ex!.GetType().Name}");
        Assert.Equal(oldDir, store.Dir);
        Assert.Single(store.List(f1));
        Assert.Single(store.List(f2));
        Assert.True(File.Exists(Path.Combine(oldDir, "manifest.json")));
        Assert.Equal(2, Directory.GetFiles(oldDir).Length - 1); // 两个备份文件 + manifest 完好
    }

    [Fact]
    public void BackupStore_RelocateTo_Success_Migrates_And_Cleans()
    {
        var store = new BackupStore(Path.Combine(_dir, "data")) { MaxPerFile = 5 };
        var oldDir = store.Dir;
        var f1 = WriteFile("甲.json", "a");
        var f2 = WriteFile("乙.json", "b");
        Assert.NotNull(store.BackupBeforeWrite(f1, out _));
        Assert.NotNull(store.BackupBeforeWrite(f2, out _));

        var newDir = Path.Combine(_dir, "backups-elsewhere");
        store.RelocateTo(newDir);

        // 阶段二提交：目录切换、manifest 落新目录、记录在新位置仍解析得到文件
        Assert.Equal(Path.GetFullPath(newDir), store.Dir);
        Assert.True(File.Exists(Path.Combine(newDir, "manifest.json")));
        Assert.Single(store.List(f1));
        Assert.Single(store.List(f2));
        Assert.Equal(2, Directory.GetFiles(newDir).Length - 1); // 两个备份文件 + manifest

        // 旧目录源文件与 manifest 清理后搬空删除（或至少已为空）
        Assert.False(Directory.Exists(oldDir) && Directory.EnumerateFileSystemEntries(oldDir).Any());
    }

    // ---------- BackupStore.Load 幽灵告警（v0.5.2 P1-2） ----------

    [Fact]
    public void BackupStore_Load_KeepsGhostRecords_And_Warns()
    {
        // 回归：缺席记录若被过滤丢弃，下一次任意写操作会把其余记录从盘上抹掉
        var dataDir = Path.Combine(_dir, "ghost-data");
        var store = new BackupStore(dataDir) { MaxPerFile = 5 };
        var file = WriteFile("幽灵.json", "v1");
        Assert.NotNull(store.BackupBeforeWrite(file, out _));
        Thread.Sleep(15);
        Assert.NotNull(store.BackupBeforeWrite(file, out _));

        // 手删磁盘上其中一个备份文件（manifest 记录仍在）
        var onDisk = Directory.GetFiles(store.Dir).Where(f => !f.EndsWith("manifest.json")).ToList();
        Assert.Equal(2, onDisk.Count);
        File.Delete(onDisk[0]);

        // 重建存储：告警带出缺席数量，幽灵记录保留在内存，List/Stats 按磁盘过滤
        var reloaded = new BackupStore(dataDir);
        Assert.NotNull(reloaded.LoadWarning);
        Assert.Contains("不可见", reloaded.LoadWarning!);
        Assert.Single(reloaded.List(file));       // 被删文件那条不进 List
        Assert.Equal(1, reloaded.Stats().count);  // 幽灵条目不计入统计
    }
}
