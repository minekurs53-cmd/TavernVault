# TavernVault 架构与流程可视化

> 配套 `docs/development-handoff.md` §2-§3 的图集。所有图为 Mermaid 源码，可在 GitHub / VS Code（Mermaid 插件）直接渲染。
> 最后更新：2026-09-05 · 对应 v1.0.0

## 1. 系统分层架构

```mermaid
graph TB
    subgraph App["TavernVault.App (WPF 进程)"]
        Shell["MainWindow<br/>WebView2 外壳"]
        subgraph Web["wwwroot 前端 (原生 JS, 无构建链)"]
            MAIN["main.js 入口<br/>主题/设置弹窗"]
            APPJ["app.js 主界面<br/>侧栏/网格/抽屉"]
            EDJ["editor.js 编辑器<br/>卡片/世界书/预设"]
            PRESET["preset-model.js<br/>预设写回纯函数"]
            APIJ["api.js fetch 封装"]
            UTIL["util.js 通用工具"]
        end
        subgraph Kestrel["Kestrel (只监听 127.0.0.1)"]
            STATIC["静态文件托管<br/>no-cache"]
            REST["ApiServer.MapApi<br/>40 个 REST 端点"]
        end
        SVCS["Services<br/>缩略图 / 文件夹选择器 /<br/>VaultWatcher 文件监视"]
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
    SVCS --> LIB
```

## 2. 启动流程

```mermaid
flowchart TD
    Start(["App.OnStartup"]) --> Parse["解析命令行参数<br/>--server / --port= / --data=<br/>--portable / --token="]
    Parse --> Build["ApiServer.Build"]
    Build --> Default{"首次运行?<br/>LibraryRoots 为空"}
    Default -->|"是"| Guess["EnsureDefaultRoot<br/>探测 %USERPROFILE%\酒馆PR（存在才注册）"]
    Default -->|"否"| Vault["new Vault(settingsStore)<br/>加载设置 + 索引 + 备份存储<br/>settings 损坏 → 告警 + 跳过自愈"]
    Guess --> Vault
    Vault --> Listen["Kestrel 监听<br/>127.0.0.1:随机或指定端口"]
    Listen --> Mode{"--server 模式?"}
    Mode -->|"是"| Headless["无窗口运行<br/>{url, token} 落盘 server-connection.json"]
    Mode -->|"否"| Window["new MainWindow(url, token)<br/>WebView2 注入令牌后打开前端"]
```

## 3. 一次"编辑角色卡并保存"的完整时序

所有覆盖写入共用这条安全链路：**备份 → 写盘 → 单文件增量更新索引**（v0.5.0 起不再全量 Rescan）。

```mermaid
sequenceDiagram
    participant U as 用户(编辑器)
    participant F as editor.js
    participant A as PUT /api/cards/{id}
    participant V as Vault
    participant B as BackupStore
    participant C as CharacterCardFile
    participant D as 磁盘

    U->>F: 修改字段并点击保存
    F->>A: {fields, tags, expectedModified}
    A->>V: Find(id)
    A->>A: CheckNotModified：<br/>文件 mtime 与 expectedModified 差 >1s → 409
    A->>D: 读原卡片 (Load)
    A->>A: 合并字段到 data 节点<br/>(JsonNode 只改目标节点)
    A->>V: BackupBeforeWrite(fullPath)
    V->>V: 判断 AutoBackup 或酒馆源<br/>(酒馆源无视开关强制备份)
    V->>B: BackupBeforeWrite + RetentionFor<br/>失败 → 响应带 warnings
    B->>D: 备份原文件到备份目录<br/>manifest.json 原子记录
    A->>C: Save(path, card)
    C->>D: PNG: 重写 chara+ccv3 两个 tEXt 块<br/>其余块字节级保留
    C->>D: JSON: SyncLegacyMirror 根级镜像同步
    A->>V: UpsertItem(fullPath)<br/>单文件增量更新 + 回填收藏/标签
    A-->>F: {ok, id, warnings, modifiedAt}
    F-->>U: 提示保存成功（有 warnings 则报错色提示）
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
    Merge --> Persist["保存 index.json<br/>(tmp+Move 原子写, 旧版留档 index.bak)"]
    Persist --> Done(["返回条目数"])

    note right of Start
        索引版本门控在 LoadIndex（启动加载时）：
        version != 4（当前 IndexVersion）直接丢弃旧索引返回空，
        由本次全量扫描重建——不在扫描尾部判断
    end note
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
        还原(v0.5.1): 先把所选备份读入内存
        再安全备份当前版本 → 满上限轮转
        不会删掉正在还原的条目; 写回原子
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
    Snap --> BK["BackupBeforeWrite<br/>(rename/move 均已接入, v0.5.2 起 move 也备份)"]
    BK --> Op["FileOperations.Rename / Move<br/>(move 先 GuardUnderRoots<br/>目标必须在库根内)"]
    Op --> RS["RemoveItem(旧路径)<br/>+ UpsertItem(新路径)<br/>+ SetUserData 迁移收藏/标签"]
    RS --> OK["{ok, id: newId}"]
```

## 9. 三逻辑库选项卡数据流（v0.4.2）

```mermaid
flowchart TD
    Meta["GET /api/meta"] --> Libs["libraries: 三逻辑库聚合<br/>{key, label, total, rootCount,<br/>favorites, kinds(8类含0), dirs, tags}<br/>normal=普通根并集 / tavernST / tavernTT"]
    Libs --> Tabs["侧栏 #lib-tabs：三库选项卡常显<br/>ST=蓝徽标 TT=绿徽标（含 rootCount=0）"]
    Tabs -->|"switchLibrary(key)"| Reset["重置 kind/dir/root/tag<br/>保留 q/fav/sort<br/>localStorage('tv-library') 校验回写"]
    Reset --> Query["GET /api/items?source=...<br/>(+dir 普通库 / +root 酒馆库)"]
    Query --> Filter["Vault.Query<br/>Source 过滤 (与 RootPath/Dir AND)"]
    Filter --> Grid["网格只显示当前库内容<br/>类型计数/收藏/标签/子目录均按当前库"]
    Tabs --> Empty0{"空态优先级"}
    Empty0 -->|"rootCount=0"| Guide["引导：Normal 添加根目录 /<br/>酒馆 一键接入 → 打开库设置"]
    Empty0 -->|"total=0"| Rescan2["建议重新扫描"]
    Empty0 -->|"筛选无结果"| FilterEmpty["换个分类或关键词"]
```

## 10. 磁盘目录拓扑

```mermaid
graph TD
    subgraph Repo["TavernVault 仓库 (源码, 位置因机器而异)"]
        SLNX["TavernVault.slnx"]
        SRC["src/"] --> CORE["TavernVault.Core/"]
        SRC --> APP["TavernVault.App/<br/>bin/Release/net10.0-windows/<br/>TavernVault.exe + wwwroot"]
        TESTS["tests/"] --> UNIT["TavernVault.Core.Tests/<br/>(数量以 dotnet test 输出为准)"]
        TESTS --> INTG["TavernVault.IntegrationTests/<br/>(进程内 Kestrel + 隔离临时库)"]
        TESTS --> NODE["preset-model.test.mjs<br/>(Node 纯函数测试)"]
        TESTS --> SMOKE["smoke_api.py<br/>(同数据目录可重复运行)"]
        DOCS["docs/ (本文档套件)"]
    end

    subgraph UserData["数据目录 (默认 %APPDATA%\TavernVault；--data 覆盖；--portable 随程序目录)"]
        SETTINGS["settings.json<br/>(损坏时坏文件留档 .corrupt-*)"]
        INDEX["index.json (version=4)<br/>+ index.bak 上一版留档"]
        BACKUPS["backups/ (可自定义位置)<br/>manifest.json 原子写"]
        THUMBS["thumbs/ 缩略图缓存(可删,自动重建)"]
        LOGS["logs/ 按日滚动, 保留 7 天"]
        URL["server-connection.json<br/>(仅 --server: {url, token})"]
    end

    subgraph Libraries["库根 (用户资源, 只读扫描+用户主动写)"]
        PR["局外资源根<br/>如 %USERPROFILE%\酒馆PR (Normal)"]
        ST["SillyTavern\data\default-user (TavernST)"]
        TT["TauriTavern\cache\default-user (TavernTT)"]
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
    v0.4.2 : 三逻辑库选项卡 : 每库独立分类+二级子目录 : 来源过滤+冷升级自愈
    v0.4.3 : 侧栏手风琴+滚动隔离(含弹窗滚动修复) : 探测去硬编码(可移植性起步)
    v0.5.0 : 深度优化：PNG另存为损坏修复+会话令牌/Host校验 : 备份告警+滚动日志+原子写 : 增量更新+并发409+单实例
    v0.5.1 : 安全加固：预设可视化XSS修复+junction/路径逃逸封堵 : 设置损坏防护+index.bak+还原自逐出修复 : 冒烟同目录可重复
    v0.5.2 : 备份Load不丢记录+RelocateTo两阶段 : 编辑器重构(互刷/Esc/409自动重扫)+move备份 : 酒馆护栏测试+错误合同补齐
    v0.5.3 : v0.5.x收尾：UI清单12项实跑(?token=通道) : 奥卡姆剃刀修剪无用代码
    v0.6.0 : 格式对齐：识别13类官方格式+主题字段修正 : 独立世界书容器保形 : 新建文件11类模板(创建即编辑)
    v0.6.1 : 分类回撤(奥卡姆剃刀) : 新建模板收敛6类 : 首次自包含打包
    v0.7.0 : 预设可视化三期(拖拽/增删/分组) : preset-model 纯函数+Node测试 : MIT 开源定位
    v0.7.1 : 酒馆只读托管+导出副本 : 修改历史+数据目录可视化
    v0.7.2 : 文件监视自动重扫(防抖+轮询) : 冒烟复审
    v0.7.3 : 收纳入库(散乱目录批量分类进库)
    v0.7.4 : 集成测试进 dotnet test : GitHub Actions CI
    v0.7.5 : README 重写 : 文档维护约定
    v0.7.6 : 内嵌书合入(次版重构为本地追加)
    v0.7.7 : 便携模式 --portable : 集成测试+2
    v0.7.8 : 应用图标初版(药瓶意象)
    v0.7.9 : 图标重设计：明亮简约·翠绿文件夹叠卡
    v1.0.0 : 首个正式版 : 隐私审计+历史重写脱敏 : 仓库公开+Release
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
