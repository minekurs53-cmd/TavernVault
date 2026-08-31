# TavernVault 架构与流程可视化

> 配套 `docs/development-handoff.md` §2-§3 的图集。所有图为 Mermaid 源码，可在 GitHub / VS Code（Mermaid 插件）直接渲染。
> 最后更新：2026-08-31 · 对应 v0.4.1

## 1. 系统分层架构

```mermaid
graph TB
    subgraph App["TavernVault.App (WPF 进程)"]
        Shell["MainWindow<br/>WebView2 外壳"]
        subgraph Web["wwwroot 前端 (原生 JS, 无构建链)"]
            MAIN["main.js 入口<br/>主题/设置弹窗"]
            APPJ["app.js 主界面<br/>侧栏/网格/抽屉"]
            EDJ["editor.js 编辑器<br/>卡片/世界书/预设"]
            APIJ["api.js fetch 封装"]
        end
        subgraph Kestrel["Kestrel (只监听 127.0.0.1)"]
            STATIC["静态文件托管<br/>no-cache"]
            REST["ApiServer.MapApi<br/>31 个 REST 端点"]
        end
        SVCS["Services<br/>缩略图 / 文件夹选择器"]
    end

    subgraph Core["TavernVault.Core (无 UI 依赖, 可单测)"]
        VAULT["Vault<br/>内存索引 + 查询 + 护栏"]
        SCANNER["LibraryScanner<br/>递归扫描 + 增量复用"]
        DETECTOR["TypeDetector<br/>内容识别 8 类"]
        TAVERND["TavernDetector<br/>酒馆安装探测"]
        STORE["SettingsStore<br/>settings/index 持久化"]
        BACKUP["BackupStore<br/>备份轮转与迁移"]
        FILEOPS["FileOperations<br/>重命名/移动/回收站"]
        CARDS["Cards<br/>PngChunkIO / CharacterCardFile / CharacterBook"]
    end

    subgraph Disk["磁盘"]
        LIB["库根目录（用户资源, 圣域）<br/>酒馆PR / SillyTavern / TauriTavern"]
        DATA["数据目录<br/>%APPDATA%\\TavernVault"]
    end

    Shell --> Web
    Web -->|"fetch /api/*"| REST
    STATIC --> Web
    REST --> VAULT
    VAULT --> SCANNER
    SCANNER --> DETECTOR
    VAULT --> STORE
    VAULT --> BACKUP
    REST --> FILEOPS
    REST --> CARDS
    REST --> SVCS
    REST --> TAVERND
    SCANNER --> LIB
    CARDS --> LIB
    FILEOPS --> LIB
    STORE --> DATA
    BACKUP --> DATA
    SVCS --> DATA
```

## 2. 启动流程

```mermaid
flowchart TD
    Start(["App.OnStartup"]) --> Parse["解析命令行参数<br/>--server / --port= / --data="]
    Parse --> Build["ApiServer.Build"]
    Build --> Default{"首次运行?<br/>LibraryRoots 为空"}
    Default -->|"是"| Guess["EnsureDefaultRoot<br/>探测 D:\\agent\\酒馆PR 等候选"]
    Default -->|"否"| Vault["new Vault(settingsStore)<br/>加载设置 + 索引 + 备份存储"]
    Guess --> Vault
    Vault --> Listen["Kestrel 监听<br/>127.0.0.1:随机或指定端口"]
    Listen --> Mode{"--server 模式?"}
    Mode -->|"是"| Headless["无窗口运行<br/>URL 落盘 server-url.txt"]
    Mode -->|"否"| Window["new MainWindow(url)<br/>WebView2 打开前端"]
```

## 3. 一次"编辑角色卡并保存"的完整时序

所有覆盖写入共用这条安全链路：**先备份 → 写盘 → 重扫**。

```mermaid
sequenceDiagram
    participant U as 用户(编辑器)
    participant F as editor.js
    participant A as PUT /api/cards/{id}
    participant V as Vault
    participant B as BackupStore
    participant C as CharacterCardFile
    participant S as LibraryScanner
    participant D as 磁盘

    U->>F: 修改字段并点击保存
    F->>A: {fields: {...}, tags: [...]}
    A->>D: 读原卡片 (Load)
    A->>A: 合并字段到 data 节点<br/>(JsonNode 只改目标节点)
    A->>V: BackupBeforeWrite(fullPath)
    V->>V: 判断 AutoBackup 或酒馆源<br/>(酒馆源无视开关强制备份)
    V->>B: BackupBeforeWrite + RetentionFor
    B->>D: 备份原文件到备份目录<br/>manifest.json 记录
    A->>C: Save(path, card)
    C->>D: PNG: 重写 chara+ccv3 两个 tEXt 块<br/>其余块字节级保留
    C->>D: JSON: SyncLegacyMirror 根级镜像同步
    A->>S: Rescan()
    S-->>A: 新条目数
    A-->>F: {ok, id}
    F-->>U: 提示保存成功
```

## 4. 扫描与增量索引

```mermaid
flowchart TD
    Start(["Rescan 触发<br/>启动/操作后/手动"]) --> Lock["lock(_lock)<br/>快照旧 Items 为字典"]
    Lock --> ForEach["遍历每个 LibraryRoot<br/>递归枚举文件(跳过点目录)"]
    ForEach --> EachFile{"每个文件"}
    EachFile --> Stable{"路径+大小+修改时间<br/>都没变?"}
    Stable -->|"是"| Reuse["复用旧条目<br/>保留收藏/我的标签"]
    Stable -->|"否"| Detect["TypeDetector 按内容识别<br/>character/lorebook/preset/<br/>theme/script/text/archive/other"]
    Detect --> Digest["抽取摘要<br/>title/creator/tags/entryCount/<br/>hasCharacterBook 等"]
    Digest --> NewItem["生成新条目<br/>Id = 完整路径哈希"]
    Reuse --> Merge["合并全部结果"]
    NewItem --> Merge
    Merge --> Version{"index.json 版本 == 3?"}
    Version -->|"否"| Drop["丢弃整个旧索引<br/>全部重建"]
    Version -->|"是"| Persist["保存 index.json<br/>+ LastScanAt"]
    Drop --> Persist
    Persist --> Done(["返回条目数"])
```

## 5. 备份生命周期（单文件视角）

```mermaid
stateDiagram-v2
    [*] --> 无备份: 文件首次入库
    无备份 --> 有备份: 覆盖写入前 BackupBeforeWrite
    有备份 --> 有备份: 再写再备<br/>(超过保留份数时删最旧)
    有备份 --> 还原中: 用户点击还原
    还原中 --> 有备份: 先备份"当前版本"<br/>再写回所选备份
    有备份 --> 无备份: 用户删除全部备份<br/>或源文件删除后 Load 过滤
    无备份 --> [*]: 源文件被删除

    note right of 有备份
        保留份数 RetentionFor:
        普通库 = 用户设置(默认5)
        TauriTavern 源 = 固定10
    end note
```

## 6. 备份位置自定义与迁移（v0.4.1）

```mermaid
flowchart TD
    Input["设置弹窗修改备份目录"] --> API["POST /api/settings/backup<br/>{backupDir: 'D:\\Backups\\TV'}"]
    API --> Empty{"目录为空串?"}
    Empty -->|"是"| Reset["SetBackupRoot(null)<br/>恢复默认 %DATA%\\backups"]
    Empty -->|"否"| Abs{"是绝对路径?"}
    Abs -->|"否"| Err400["400: 必须是绝对路径"]
    Abs -->|"是"| Migrate["SetBackupRoot(dir)"]
    Migrate --> Move["BackupStore.RelocateTo(dir)<br/>移动全部备份文件 + manifest.json"]
    Move --> Clean["旧目录搬空后删除"]
    Clean --> Save["settings.json 记录 BackupRootPath"]
    Save --> Result["stats 返回 dir 与 defaultDir<br/>前端区分显示"]
    Reset --> Save
```

## 7. 酒馆接入流程（v0.4.0）

```mermaid
sequenceDiagram
    participant U as 用户
    participant F as 设置弹窗·接入向导
    participant D as POST /api/tavern/detect
    participant TD as TavernDetector
    participant C as POST /api/tavern/connect
    participant V as Vault

    U->>F: 点击「检测酒馆」
    F->>D: {}
    D->>TD: DetectAll()
    TD->>TD: SillyTavern→data\default-user<br/>TauriTavern→cache\default-user<br/>(需含 characters/)
    TD-->>D: 来源+标签+子目录清单
    D-->>F: {found: [...]}
    F-->>U: 展示可接入的子目录
    U->>F: 勾选并确认接入
    F->>C: {source: "tavernST"}
    C->>TD: BuildRoots(baseDir, source)
    C->>V: 逐个 AddRoot(去重)
    C->>V: Rescan()
    C-->>F: {added: N, roots: [...]}
    F-->>U: 侧栏出现带徽标的库分组
```

## 8. 重命名/移动护栏决策树

```mermaid
flowchart TD
    Req["POST /api/items/{id}/rename 或 /move"] --> Find["vault.Find(id)"]
    Find --> Guard{"item.RootSource<br/>!= Normal ?"}
    Guard -->|"否(普通库)"--> Snap["取用户数据快照<br/>(收藏+标签)"]
    Guard -->|"是(酒馆源)"--> Force{"请求带 force:true ?"}
    Force -->|"否"| R403["403: 酒馆聊天按文件名/路径<br/>引用角色卡, 拒绝"]
    Force -->|"是"| Confirm["前端已弹风险确认框"] --> Snap
    Snap --> BK["BackupBeforeWrite"]
    BK --> Op["FileOperations.Rename / Move<br/>(move 先 GuardUnderRoots<br/>目标必须在库根内)"]
    Op --> RS["Rescan()"]
    RS --> Migrate["SetUserData(newId, 快照)<br/>Id 随路径变化, 迁移用户数据"]
    Migrate --> OK["{ok, id: newId}"]
```

## 9. 库根分组浏览数据流（v0.4.1）

```mermaid
flowchart LR
    Meta["GET /api/meta"] --> Roots["roots: [{path, source, count}]"]
    Roots --> Sidebar["侧栏「库」分区<br/>每根一行: 名称+徽标+count<br/>ST=蓝 TT=绿"]
    Sidebar -->|"点击某库"| State["state.filter.root = path"]
    State --> Query["GET /api/items?root=..."]
    Query --> Filter["Vault.Query<br/>RootPath 精确匹配(忽略大小写)"]
    Filter --> Grid["网格只显示该库内容<br/>顶部筛选栏显示当前库名"]
    Sidebar -->|"点击「全部」"| Clear["root=null → 全库视图"]
```

## 10. 磁盘目录拓扑

```mermaid
graph TD
    subgraph Repo["D:\agent\TavernVault (源码)"]
        SLNX["TavernVault.slnx"]
        SRC["src/"] --> CORE["TavernVault.Core/"]
        SRC --> APP["TavernVault.App/<br/>bin/Release/net10.0-windows/<br/>TavernVault.exe + wwwroot"]
        TESTS["tests/"] --> UNIT["TavernVault.Core.Tests/ (36项)"]
        TESTS --> SMOKE["smoke_api.py (49项)"]
        DOCS["docs/ (本文档套件)"]
    end

    subgraph UserData["%APPDATA%\TavernVault (数据目录)"]
        SETTINGS["settings.json"]
        INDEX["index.json (version=3)"]
        BACKUPS["backups/ (可自定义位置)<br/>└ manifest.json + 各文件子目录"]
        THUMBS["thumbs/ 缩略图缓存(可删,自动重建)"]
        URL["server-url.txt (仅 --server)"]
    end

    subgraph Libraries["库根 (用户资源, 只读扫描+用户主动写)"]
        PR["D:\agent\酒馆PR (Normal)"]
        ST["D:\agent\SillyTavern\data\default-user (TavernST)"]
        TT["D:\agent\TauriTavern\cache\default-user (TavernTT)"]
    end

    APP -.->|扫描/编辑| Libraries
    APP -.->|设置/索引/备份/缩略图| UserData
```

## 11. 版本演进

```mermaid
timeline
    title TavernVault 版本演进
    v0.1.0 : 初版：扫描/分类/搜索/编辑/文件操作
    v0.2.0 : 内嵌世界书 : 增量扫描 : 索引版本门控 : 用户数据迁移
    v0.3.0 : 另存为 : 备份与还原 : 预设可视化一期
    v0.3.1 : 库设置修复 : Esc 兜底 : 预设可视化二期
    v0.4.0 : 酒馆接入 : 库根来源标记 : 检测/接入端点 : 安全护栏
    v0.4.1 : 侧栏库分组选项卡 : 备份位置自定义 : 项目文档体系
```

## 12. 风险与防护措施对照

```mermaid
quadrantChart
    title 写操作风险 × 防护强度
    x-axis "防护弱" --> "防护强"
    y-axis "低风险" --> "高风险"
    "普通库编辑保存": [0.75, 0.35]
    "另存为新文件": [0.85, 0.15]
    "删除文件": [0.9, 0.6]
    "酒馆源编辑": [0.7, 0.75]
    "酒馆源重命名/移动": [0.95, 0.9]
```

说明：删除走系统回收站（可找回）；酒馆源重命名/移动是唯一"默认拒绝"的操作（403 + force 确认 + 强制备份 + TT 高保留份数四重防护）。
