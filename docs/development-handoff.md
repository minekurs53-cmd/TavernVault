# TavernVault 项目技术文档

> 定位：本项目的**权威技术文档**，由 v0.4.0 开发交接文档升级而来。任何新接手的 agent / 开发者读完本文即可理解项目全貌、继续开发、避开已知坑，无需翻会话历史。
>
> | 配套文档 | 用途 |
> |---|---|
> | `README.md` | 项目门面：功能介绍 + 文档导航 |
> | `docs/architecture-visualization.md` | 架构与流程图集（Mermaid，10 张） |
> | `docs/quick-reference.md` | 速查手册：命令 / API / 数据格式坑 / 故障排查 |
> | `docs/st-sync-feasibility.md` | 酒馆接入可行性分析（历史决策依据） |
>
> 当前版本：**v0.4.2（工作区，未提交）** · 最后更新：2026-08-31

---

## 1. 项目定位

为 SillyTavern（酒馆）玩家做的 **Windows 桌面资源管理器**：把散落在文件夹里的角色卡 / 世界书 / 预设 / 美化主题 / 脚本集中索引、检索、编辑、整理，并与本机部署的酒馆项目打通（v0.4.0 起）。

- 源码：`D:\agent\TavernVault`
- 用户真实资源库（只读扫描，绝不主动改动）：`D:\agent\酒馆PR`
- 两个酒馆项目（接入目标，见 `docs/st-sync-feasibility.md`）：
  - 原版 SillyTavern：`D:\agent\SillyTavern`，数据在 `data\default-user\`
  - TauriTavern：`D:\agent\TauriTavern`，数据在 `cache\default-user\`（`default\` 是出厂副本，禁止编辑）

一句话设计哲学：**用户的资源文件是圣域**——程序只做"读内容建索引 + 用户主动触发才写"，所有覆盖写入前自动备份，删除进回收站，酒馆源文件额外加护栏。

---

## 2. 技术栈与总体架构

| 层 | 技术 | 说明 |
|---|---|---|
| 桌面外壳 | WPF + WebView2 | Win10/11 自带运行时；`--server` 模式可无窗口运行 |
| 本地服务 | .NET 10 + ASP.NET Core (Kestrel) | REST API + 静态托管，**只绑定 127.0.0.1** |
| 前端 | 原生 HTML/CSS/JS | 无 npm、无构建链，改完即生效 |
| JSON 处理 | System.Text.Json (`JsonNode`) | 无损编辑：保留文件中的未知字段 |

三个工程（`TavernVault.slnx`）：

| 工程 | 职责 |
|---|---|
| `src/TavernVault.Core` | 无 UI 依赖的核心库：PNG 数据块、角色卡/内嵌书读写、类型识别、扫描索引、设置/索引/备份持久化、文件操作。可单测 |
| `src/TavernVault.App` | WPF 外壳 + `Hosting/ApiServer.cs`（全部 REST 端点）+ `wwwroot` 前端 + `Services`（缩略图、文件夹选择器） |
| `tests/TavernVault.Core.Tests` | xUnit 单元测试（当前 36 项） |

### 请求处理链路

```
WebView2 (wwwroot 前端, 5 个 JS 模块)
    │ fetch http://127.0.0.1:<port>/api/...
    ▼
Kestrel → ApiServer.MapApi（全部端点，统一 Handle/HandleAsync 包装错误）
    ▼
Vault（内存索引 + 查询，单例，内部 _lock 保护）
    ├─ LibraryScanner  → 扫描库目录、增量复用、TypeDetector 内容识别
    ├─ SettingsStore   → settings.json / index.json 持久化（索引版本门控）
    ├─ BackupStore     → 备份 manifest 与轮转
    └─ FileOperations  → 重命名/移动/回收站/路径防护/另存为命名
    ▼
磁盘：库根目录（用户资源）+ 数据目录（%APPDATA%\TavernVault）
```

### 关键文件索引

Core：
- `Cards/PngChunkIO.cs` — PNG 分块读写（tEXt 替换/插入、CRC 重算、`WriteTexts` 单次重写多块）
- `Cards/CharacterCardFile.cs` — 角色卡加载/保存（PNG 内嵌 chara+ccv3；JSON 根级 V1 镜像同步 `SyncLegacyMirror`）
- `Cards/CharacterBook.cs` — 内嵌世界书 Spec V2 ↔ ST 内部格式双向映射（`Raw` 原样保留未编辑字段）
- `Detection/TypeDetector.cs` — 基于内容的类型识别（8 类）
- `Detection/TavernDetector.cs` — 本机 SillyTavern/TauriTavern 安装检测（纯静态，无状态）
- `Models/ItemKind.cs` — 8 种资源类型 + kind 键/中文标签映射
- `Models/LibraryRoot.cs` — 库根模型 `{Path, Source}` + `LibrarySource` 枚举（Normal/TavernST/TavernTT）+ 兼容旧字符串格式的 JsonConverter
- `Models/AppSettings.cs` — 设置模型（含 `BackupRootPath` 自定义备份位置）
- `Models/LibraryItem.cs` — 索引条目模型（含 `RootSource`、用户数据）
- `Scanning/LibraryScanner.cs` — 递归扫描 + **增量复用**（路径+大小+修改时间不变则复用旧条目）+ 点目录过滤
- `Storage/Vault.cs` — 内存索引 + 查询 + 用户数据快照迁移 + `BackupBeforeWrite` + `SetBackupRoot`
- `Storage/SettingsStore.cs` — 设置/索引持久化（索引带 `version` 门控，当前版本 3）
- `Storage/BackupStore.cs` — 文件级备份（manifest.json、按文件保留份数、还原前再备份、`RelocateTo` 迁移）
- `FileOps/FileOperations.cs` — 重命名/移动/回收站/路径防护/`GetSaveAsPath` 自动命名

App：
- `App.xaml.cs` — 启动流程：解析参数 → 起 Kestrel → 窗口模式开 MainWindow / 无窗口模式落盘 server-url.txt
- `Hosting/ApiServer.cs` — 全部端点（见 §4）+ `EnsureDefaultRoot` 首次运行默认库
- `MainWindow.xaml.cs` — WebView2 外壳
- `Services/ThumbnailService.cs` — PNG 卡片缩略图缓存（`thumbs\`）
- `Services/FolderPicker.cs` — 原生文件夹选择框（无窗口模式下禁用）
- `wwwroot/js/main.js` — 入口（主题/启动/设置弹窗/版本号）
- `wwwroot/js/app.js` — 主界面（侧栏类型+**库分组选项卡**/网格/列表/抽屉/备份弹窗）
- `wwwroot/js/editor.js` — 编辑器（角色卡表单+原始JSON、世界书条目、内嵌书、预设可视化、原文）
- `wwwroot/js/api.js` — fetch 封装（`get/post/put/del` 独立导出 + `api` 对象）
- `wwwroot/js/util.js` — 通用工具函数

---

## 3. 核心技术原理

本章讲"为什么这样设计 + 怎么实现的"。流程图见 `docs/architecture-visualization.md`。

### 3.1 无损 JSON 编辑（JsonNode，不用强类型 DTO）

酒馆资源 JSON 字段极不规范：不同版本、不同导出工具的文件字段差异大，且用户文件里常有程序不认识的字段。
若用强类型 DTO 反序列化→改→序列化，**未知字段会全部丢失**，写回后 ST 可能无法识别。
因此所有编辑路径（角色卡/世界书/预设/原文）都基于 `System.Text.Json.Nodes.JsonNode`：
只改动明确要改的节点，其余节点原样保留，写回时保持原始结构。

### 3.2 PNG 角色卡读写（tEXt 数据块）

角色卡 PNG 把卡片 JSON 以 base64 存在 `tEXt` 块（关键字 `chara`=V2、`ccv3`=V3）里。
`PngChunkIO` 逐块解析 PNG（长度+类型+数据+CRC），读写时：

- **写**：替换/插入目标 tEXt 块并重算 CRC，其余块（图像 IDAT 等）字节级原样保留——**图像不损毁**。
- `WriteTexts` 一次重写多个块：保存卡片时 chara 与 ccv3 **同步更新**，避免两块数据不一致。
- 已知边界：仅支持 tEXt；zTXt/iTXt 形式极罕见，未实现。

### 3.3 角色卡规范兼容（V1 平铺 / V2 spec+data / ST 导出镜像）

加载时 `GetDataNode` 统一取到 `data` 节点（V1 卡片则把根级字段视为 data）。
保存 JSON 时 `SyncLegacyMirror` 把 `data.*` 关键字段镜像到根级——ST 导出的卡片带根级镜像，
不同步会产出 data 与根级不一致的坏文件。

### 3.4 内嵌世界书双向映射（Spec V2 ↔ ST 内部格式）

`data.character_book.entries` 存在两种条目格式（详见 §5）。`CharacterBook` 的策略：

- **读**：统一转成 ST 内部格式给编辑器用；若原条目是 Spec V2，把原条目存进 `Raw`。
- **写**：Spec 条目只合并被编辑的字段，`Raw` 里的 `id/selective/use_regex/extensions` 等原样保留；
  entries **容器形态（数组/对象）不变**——有的工具按数组解析，改成对象会坏。

这是全项目最容易踩坑的模块，改动前先读 `tests/TavernVault.Core.Tests/CharacterBookTests.cs`（10 项）。

### 3.5 扫描与增量索引

- **内容识别**（`TypeDetector`）：按文件内容而非文件夹名判断 8 类（character/lorebook/preset/theme/script/text/archive/other）。
- **条目 Id**：完整路径的哈希（`LibraryScanner.ComputeId`）。路径变了 Id 就变，所以重命名/移动后要做用户数据迁移。
- **增量复用**：路径+大小+修改时间都没变的文件直接复用旧条目（含收藏/标签），大库重扫从秒级降到毫秒级。
- **索引版本门控**（`SettingsStore.IndexVersion = 3`）：条目模型变化时 +1，旧索引直接丢弃全量重建，
  避免增量扫描复用缺少新字段的旧条目（v0.4.0 加 `RootSource` 时就靠这个机制 2→3）。
- **点目录过滤**：跳过 `.git` 等隐藏目录。

### 3.6 备份系统

- **触发**：所有覆盖写入（编辑保存/还原/重命名）前调 `Vault.BackupBeforeWrite`。
  开了 AutoBackup 就备份；**酒馆来源文件无视开关强制备份**。
- **存储**：备份目录（默认 `%DATA%\backups\`，可在设置中改为任意绝对路径）下按源文件名建子目录，
  `manifest.json` 记录全部备份元数据。`BackupStore.Load` 时过滤掉磁盘上已不存在的条目。
- **保留策略**：`RetentionFor` 钩子按库根来源决定份数——普通库用用户设置（默认 5），TauriTavern 源固定 10 份。
- **还原**：还原前把**当前文件再备份一次**，所以还原本身也可撤销。
- **自定义位置**（v0.4.1）：`Vault.SetBackupRoot(path)` 写 `AppSettings.BackupRootPath` 并调
  `BackupStore.RelocateTo` 把现有备份文件+manifest 整体搬到新目录，旧目录搬空后删除；传 null 恢复默认位置。
  API 侧要求绝对路径（相对路径 400），`/api/backups/stats` 同时返回 `dir`（当前）与 `defaultDir`（默认）供前端回显。

### 3.7 酒馆接入与安全护栏（v0.4.0）

- **库根来源标记**：`LibraryRoot{Path, Source}`，Source ∈ {Normal=0, TavernST=1, TavernTT=2}。
  旧版纯字符串数组设置由 `LibraryRootConverter` 自动迁移为 Normal。
- **检测**：`TavernDetector` 探测本机安装——ST 看 `data\default-user`（需含 `characters/`），TT 看 `cache\default-user`。
- **接入**：`POST /api/tavern/connect` 按来源把酒馆的 characters/worlds/Settings/presets 等子目录
  批量注册为带来源标记的库根（去重 + 自动重扫）。
- **护栏**：`RootSource != Normal` 的条目重命名/移动默认 **403**（酒馆聊天记录按文件名/路径引用角色卡），
  请求体带 `force:true` 才放行（前端弹风险确认框）。写前强制备份 + TT 高保留份数兜底。
- **默认库**：首次运行 `EnsureDefaultRoot` 自动把 `D:\agent\酒馆PR`（或 `%USERPROFILE%\酒馆PR`）注册为普通库。

### 3.8 三逻辑库选项卡（v0.4.1 → v0.4.2）

v0.4.1 引入库根分组（侧栏逐根列出），但接入酒馆后每个酒馆注册 5 个根（characters/worlds/OpenAI Settings/themes/regex），加局外普通根共 11+ 个平铺，且类型计数仍是全局的——文件管理依然混乱。v0.4.2 重构为**三个逻辑库选项卡，互相独立**：

- **逻辑库 = 库根来源的并集**：局外存储（全部 Normal 根）/ SillyTavern（全部 TavernST 根）/ TauriTavern（全部 TavernTT 根）。无「全部资源」总览，默认进入局外存储。
- **`Vault.BuildLibraries()`**（Core 层，可单测）聚合每库的 `total/rootCount/favorites/kinds(8 类含 0)/dirs/tags`：普通库 dirs 按 `RelativeDir` 跨根聚合（root=null），酒馆库 dirs 按注册根逐条列出（含空根 count=0）。`/api/meta` 纯增量加 `libraries` 键；全局 `kinds` 改为三库求和；全局 `roots/userTags` 保留（设置弹窗与移动弹窗依赖含空根的完整根清单）。
- **`QueryParams.Source`**：按来源过滤，与 `RootPath` 同设为 AND。`/api/items?source=` **严格契约**：非 {normal,tavernST,tavernTT} 返回 400，绝不静默当作 Normal。
- **切库重置契约**：切库重置 `kind/dir/root/tag`，保留 `q/fav/sort`。**筛选不持久化**（刷新即重置）；唯一持久化键 `localStorage('tv-library')`，启动时校验 ∈ 三值，非法**立即回写** normal。
- **二级子目录**：酒馆库二级用 `root` 参数（`RelativeDir` 相对各根，characters 根与 worlds 根的顶层文件 RelativeDir 均为 `""`，dir 参数无法区分功能分区）；普通库二级用 `dir` 参数。均与 `source` AND 叠加。
- **空态优先级**：`rootCount===0` → 引导（Normal「添加根目录」/ 酒馆「一键接入」，都开库设置）；`rootCount>0 && total===0` → 建议重扫；有筛选无结果 → 筛选空态。
- **冷升级自愈**：增量复用分支无条件刷新 `RootSource`（LibraryScanner.cs:76）+ 前端 boot 每次启动 rescan（main.js），但 `--server` 冷启动与手改索引不会被覆盖。`Vault` 构造时校验 `RootContaining(FullPath)?.Source != item.RootSource` 即触发一次 Rescan（O(n×roots)），兜住来源漂移。

---

## 4. REST API 参考

全部端点定义在 `ApiServer.MapApi`。错误统一 `{ "error": "..." }`，IO/权限类异常由 `Handle/HandleAsync` 包装为 400。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/meta` | 总数、分类计数（三库求和）、用户标签、roots（含来源与 count）、**libraries（三逻辑库聚合：total/rootCount/favorites/kinds/dirs/tags）**、版本号 |
| POST | `/api/rescan` | 全量重扫，返回条目数 |
| GET | `/api/items` | 条目查询。参数：`kind,q,tag,fav,sort(name|modified|size|kind),dir,root,source`。**source 非法值 400**，与 root AND |
| GET | `/api/items/{id}` | 单条目 |
| GET | `/api/thumb/{id}` | 角色卡缩略图（JPEG） |
| GET | `/api/image/{id}` | 角色卡原图 PNG（支持 Range） |
| GET/PUT | `/api/cards/{id}` | 角色卡读取/保存。PUT body：`{fields, alternateGreetings, tags}` 增量合并或 `{card}` 整卡替换 |
| POST | `/api/cards/{id}/saveas` | 另存为新文件（PNG 先复制原图再重嵌数据） |
| GET/PUT | `/api/cards/{id}/book` | 内嵌世界书读/写。条目带 `raw` 时保形合并 |
| POST | `/api/cards/{id}/book/saveas` | 内嵌世界书导出为独立世界书 |
| GET/PUT | `/api/lore/{id}` | 世界书读/写（整体重建 entries，其它顶层键保留） |
| POST | `/api/lore/{id}/saveas` | 世界书另存为 |
| GET/PUT | `/api/text/{id}` | 文本/原始 JSON 读/写（.json 保存前校验） |
| POST | `/api/text/{id}/saveas` | 文本另存为 |
| GET | `/api/items/{id}/backups` | 该文件的备份列表 |
| POST | `/api/backups/{bid}/restore` | 还原备份（还原前先备份当前） |
| DELETE | `/api/backups/{bid}` | 删除一个备份 |
| GET | `/api/backups/stats` | 备份统计：count/bytes/autoBackup/maxPerFile/dir/defaultDir |
| POST | `/api/settings/backup` | 备份设置。body：`{autoBackup, maxPerFile(1-50), backupDir}`（空串=恢复默认位置，必须绝对路径） |
| POST | `/api/items/{id}/favorite` | 收藏切换 |
| POST | `/api/items/{id}/tags` | 设置用户标签 |
| POST | `/api/items/{id}/rename` | 重命名。酒馆源默认 403，`force:true` 放行；自动迁移收藏/标签 |
| POST | `/api/items/{id}/move` | 移动（可跨库根）。同上护栏；目标根必须在已登记库根内 |
| POST | `/api/items/{id}/delete` | 删除（进系统回收站） |
| POST | `/api/reveal` | 资源管理器中显示文件 |
| POST | `/api/roots` | 添加库根 `{path, source}` |
| DELETE | `/api/roots` | 移除库根 `{path}`（不动文件本身） |
| POST | `/api/tavern/detect` | 检测本机酒馆安装与可接入子目录 |
| POST | `/api/tavern/connect` | 按来源批量注册酒馆子目录为库根 |
| POST | `/api/pick-folder` | 原生文件夹选择框（无窗口模式返回 400） |
| GET | `/api/categories` | 按根+相对目录聚合的目录计数（用于旧目录筛选） |

---

## 5. 数据格式要点（接手编辑逻辑必读）

- **角色卡**：PNG 内嵌 `tEXt` 块 `chara`/`ccv3`（base64 JSON，V2/V3）；或 JSON（V2 `spec+data`，或 V1 平铺，或 ST 导出带根级镜像字段）。编辑 `data`，保存时 PNG 一次重写两块；JSON 同步根级镜像。
- **内嵌世界书**：`data.character_book.entries`。两种条目格式：
  - Spec V2：`keys/secondary_keys/enabled/insertion_order/position("before_char"|"after_char")/extensions`
  - ST 内部：`key/keysecondary/disable/order/position(0-6)/depth/probability`
  - 读取时统一转 ST 格式并把 Spec 原条目放 `Raw`；写回时 Spec 条目只合并被编辑字段，`Raw` 里的 `id/selective/use_regex/extensions` 等原样保留。**容器形态（数组/对象）不变。**
- **预设**：`prompts[]` + `prompt_order[]`。⚠️ **`prompt_order[i].order[j]` 的启用字段是 `enabled`，不是 `enable`**（真实 ST 文件实测）。`prompts[j].system_prompt===true` 表示系统管理项（内容只读）。
- **世界书**：`entries` 为对象（键=索引）或数组；ST 格式字段 `key/keysecondary/content/comment/constant/disable/order/position/depth/probability`。
- **库根（settings.json）**：`LibraryRoots` 为对象数组 `[{"Path":"...","Source":0|1|2}]`（0=Normal、1=TavernST、2=TauriTavern）。`LibraryRootConverter` 反序列化时兼容旧版纯字符串数组（自动按 Normal 处理）。索引版本已升到 3（条目新增 `RootSource`），旧索引会丢弃重建。
- **酒馆护栏**：`rootSource != 0` 的条目默认禁止重命名/移动（API 返回 403），请求体带 `force:true` 才放行；酒馆源文件写前强制备份（忽略自动备份开关），TT 源备份保留 10 份。

---

## 6. 数据目录与持久化

数据目录默认 `%APPDATA%\TavernVault\`，可用 `--data=<目录>` 覆盖（测试用）：

```
%APPDATA%\TavernVault\
├─ settings.json    # AppSettings：LibraryRoots / UiTheme / AutoBackup / MaxBackupsPerFile / BackupRootPath
├─ index.json       # 索引：{ version: 3, items: [LibraryItem...] }，版本不符则丢弃重建
├─ backups\         # 默认备份目录（可自定义到任意位置）
│  ├─ manifest.json # 全部备份元数据
│  └─ <源文件名>\   # 每个源文件一个子目录存放各版本备份
├─ thumbs\          # 角色卡缩略图缓存（可整体删除，会自动重建）
└─ server-url.txt   # 仅 --server 模式：实际监听 URL，供脚本读取
```

`LibraryItem` 主要字段：`id`（路径哈希）、`fileName/fullPath/rootPath/rootSource/relativeDir`、
`kindValue`+`kind`（枚举值+小写键）、内容摘要（`title/creator/version/description/tags/entryCount/hasEmbeddedCard/hasCharacterBook`）、
用户数据（`favorite/userTags`，重扫描时保留）。

---

## 7. 运行与构建

```bash
dotnet build TavernVault.slnx -c Release
# 窗口模式
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe
# 无窗口（调试/测试）
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe --server --port=47999 [--data=<目录>]
```

命令行参数（`App.xaml.cs` → `ApiServer.Build`）：

| 参数 | 作用 |
|---|---|
| `--server` | 无窗口模式；URL 同时写入数据目录 server-url.txt |
| `--port=<n>` | 指定端口；缺省时随机分配（`127.0.0.1:0`） |
| `--data=<目录>` | 覆盖数据目录。⚠️ Git Bash 下绝对路径反斜杠会被转义吞掉，**用相对路径或正斜杠** |

版本号来自 csproj `<Version>`（当前 0.4.1），经 `/api/meta.version` 显示在界面左下角。

### 构建/运行铁律（踩过的坑）

1. **先杀进程再构建**：`taskkill -IM TavernVault.exe -F`（Git Bash 写法；cmd 为 `taskkill /IM TavernVault.exe /F`）。运行中的 exe 会锁 DLL，导致构建"成功"但产物是旧的（曾因此误判为前端 bug）。
2. **`dotnet build` 增量构建偶发不拷贝 wwwroot**。已在 `TavernVault.App.csproj` 加 `CopyFrontendFiles` Target（AfterTargets=Build，按时间戳强制同步）。若仍怀疑前端没更新，直接对比 `bin/.../wwwroot` 与 `src/.../wwwroot` 的时间戳/大小。
3. **仓库体积会悄悄增长**：窗口模式运行时 WebView2 把浏览器缓存写在 `bin/.../TavernVault.exe.WebView2\`（曾积累 69M）。该目录可随时整体删除（自动重建）；`--server` 无窗口模式不产生。
4. **前端语法校验必须用 `.mjs`**：`node --check x.js` 按 script 模式，抓不到模块级错误（如非 async 函数里用 await、重复 import）。正确做法：`cp x.js /tmp/x.mjs && node --check /tmp/x.mjs`。
5. **GitHub 直连失败**（国内网络）：本仓库已配置 `git config http.proxy http://127.0.0.1:7897`（用户系统代理）。推送前若报 `Failed to connect to github.com`，确认代理在监听 7897。

---

## 8. 测试体系

| 层级 | 命令 / 入口 | 数量 | 说明 |
|---|---|---|---|
| 单元测试 | `dotnet test TavernVault.slnx -c Release` | 41 项 | PNG 块、内嵌书映射、备份/另存为/自定义备份位置、增量扫描、用户数据迁移、**来源过滤与 BuildLibraries 聚合（VaultQueryTests，含冷升级自愈）** |
| API 冒烟 | `python tests/smoke_api.py` | 61 项 | **夹具自足（脚本自建 testdata 并注册）**；先 `--server --port=47999 --data=<临时目录>` 再跑；**写操作只作用于 testdata** |
| UI 冒烟 | 浏览器自动化打开 `http://127.0.0.1:47999/` | — | 页面加载失败时读 `window.__errs`（index.html 内置探针）；截图存 `ui-shots/`（已 gitignore） |
| 真实库验证 | `GET` 任意端点 | — | 只读核对可以，**绝不对真实库 PUT/POST** |

单元测试文件分布：CharacterBookTests(10)、CardAndDetectionTests(9)、VaultQueryTests(5)、BackupAndSaveAsTests(6)、PngChunkIOTests(5)、ScannerAndFileOpsTests(5)、UnitTest1(1)。

---

## 9. 开发历程与项目状态

### 9.1 已完成（版本历程）

| 版本 | 主题 | 主要内容 |
|---|---|---|
| v0.1.0 | 初版 | 扫描/内容识别分类/搜索/收藏/标签/重命名/移动/回收站/角色卡表单+原始JSON/世界书条目/原文编辑器/深浅色/网格列表 |
| v0.2.0 | 内嵌世界书 | 内嵌世界书识别+编辑；重命名移动用户数据迁移；JSON 根级镜像同步；增量扫描；索引版本门控；Esc 级联修复 |
| v0.3.0 | 另存为 + 备份 | 另存为（自动命名 `原名-副本 yyyy-MM-dd_HHmmss`）；备份与还原；预设可视化一期（只读）；`docs/st-sync-feasibility.md` |
| v0.3.1 | 打磨 | 库设置修复；Esc 兜底关弹窗；左下角版本号；预设可视化二期（采样参数/生效顺序/提示词详情可编辑）；csproj 确定性拷贝 |
| v0.4.0 | 酒馆接入 | 库根模型对象化 `{Path,Source}`+旧设置自动迁移（索引 2→3）；`TavernDetector`；`/api/tavern/detect+connect`；酒馆源重命名/移动 403 护栏（`force` 覆盖）；酒馆源强制备份、TT 保留 10 份；前端接入向导+来源徽章 |
| v0.4.1 | **多库管理 + 备份位置** | 侧栏"库"分组选项卡（按库根浏览）；备份位置自定义（`BackupRootPath` + `RelocateTo` 整体迁移 + 绝对路径校验）；项目文档体系升级；单元测试 34→36 |
| **v0.4.2（当前工作区）** | **三逻辑库选项卡** | 侧栏重构为三个独立逻辑库（局外存储/SillyTavern/TauriTavern，来源并集）；每库独立类型计数 + 二级子目录导航（酒馆库按功能分区根）；`Vault.BuildLibraries` 聚合 + `QueryParams.Source` 过滤（非法 source 400）；移动弹窗按来源分组；构造时来源漂移自愈；单测 36→41、冒烟 49→61（**夹具自足**） |

### 9.2 当前状态（截至 2026-08-31）

- 分支 `qoder/TavernVault`，最新提交为 v0.4.1。
- **工作区有 10 个未提交文件**（即 v0.4.2 全部改动）：`ApiServer.cs`、`TavernVault.App.csproj`（0.4.2）、`index.html`、`app.css`、`app.js`、`main.js`、`Vault.cs`、`smoke_api.py`（修改）+ `LibraryInfo.cs`、`VaultQueryTests.cs`（新增）。**待提交。**
- v0.4.2 验证情况：Release 构建 0 警告 0 错误；41/41 单测通过；61/61 冒烟通过（含三逻辑库 12 项）；浏览器 UI 清单 7 项通过（三 tab 常显、切库重置/保留契约、tv-library 非法回写、两类空态引导、二级子目录与组合过滤、移动弹窗分组、0 JS 错误）。

### 9.3 Git 信息

- 远程：`origin https://github.com/minekurs53-cmd/TavernVault.git`（私有）
- 已推送提交：`8f2c055`(v0.1.0) → `a1ad1d9`(v0.2.0) → `3e22813`(v0.3.0) → `2de7a6e`(v0.3.1) → `66c2781`(v0.4.0)
- 当前分支：`qoder/TavernVault`（注意不是 main；推送时确认目标分支）

---

## 10. 已知限制

1. PNG 卡片仅支持 tEXt 内嵌形式（zTXt/iTXt 极少见，未写）。
2. 移动/重命名后"收藏/我的标签"靠快照迁移，**已覆盖应用内操作**；外部文件管理器改名仍会丢（需要内容指纹追踪，见 §11）。
3. 备份按"文件名"归档：两个同名不同目录的文件备份会混在同一子目录（manifest 记录了完整路径，还原不受影响，但列表会混）。
4. 扫描是手动/操作后触发，无 FileSystemWatcher，外部改动需手动重扫。
5. 界面文案与格式字段面向 SillyTavern 主流格式；非标准文件落到"文本/其他"分类，不会出错。

---

## 11. 未完成与未来开发方向

### 未完成（当前收尾项）

- [ ] **提交 v0.4.2**（10 个文件已验证，待用户确认后 commit；推送走代理 7897）

### 近期方向（下一两个迭代）

1. **预设可视化三期**：拖拽排序（写 `prompt_order.order` 数组）、新增/删除提示词（系统项 `system_prompt===true` 防误删）、角色分组切换。
2. **内嵌世界书 ← 独立世界书合入**：导出已做（`/api/cards/{id}/book/saveas`），反向导入未做。
3. **酒馆接入增强**：聊天记录 → 角色卡反向引用检查（改名前提示哪些聊天会断链）；接入子目录白名单可配置。

### 中远期方向

4. **重复资源检测**：内容指纹（如哈希）识别同一资源的多个副本，配合整理建议。
5. **FileSystemWatcher**：监视库目录变化自动重扫；配套批量操作（多选移动/打标）。
6. **内容指纹追踪用户数据**：外部改名后收藏/标签仍能找回（替代纯路径哈希 Id）。
7. **形态扩展**：Core 层无 UI 依赖，可直接复用做 CLI 或托盘工具。

---

## 12. 接手开发 Checklist

- [ ] 读本文档 + `README.md` + `docs/quick-reference.md`（速查）+ `docs/st-sync-feasibility.md`（酒馆背景）
- [ ] `taskkill` 旧进程 → `dotnet build TavernVault.slnx -c Release` → 确认 bin/wwwroot 时间戳最新
- [ ] 前端改动后用 `.mjs` 方式 `node --check` 全部 5 个 js（api/app/editor/main/util）
- [ ] 改动 Core 后跑 `dotnet test`；改动 API 后跑 `smoke_api.py`（临时 data 目录，**注意 Git Bash 下 `--data` 用相对路径**）
- [ ] UI 改动用浏览器截图核对，读 `window.__errs` 确认无模块错误
- [ ] 提交前 `git status` 确认分支（`qoder/TavernVault`）；推送走代理 7897
- [ ] 任何写操作只在 `testdata/` 临时目录验证，真实库只读
- [ ] 改动条目模型（`LibraryItem`）记得 `IndexVersion` +1，否则旧索引增量复用会缺新字段

---

**文档版本**：2.1 · **最后更新**：2026-08-31 · 对应程序版本 v0.4.2
