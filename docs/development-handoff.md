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
> 当前版本：**v0.7.2** · 最后更新：2026-09-05

---

## 1. 项目定位

为 SillyTavern（酒馆）玩家做的 **Windows 桌面资源管理器**：把散落在文件夹里的角色卡 / 世界书 / 预设 / 美化主题 / 脚本集中索引、检索、编辑、整理，并与本机部署的酒馆项目打通（v0.4.0 起）。

- 源码：本仓库根目录（`TavernVault.slnx` 所在目录）
- 用户真实资源库（只读扫描，绝不主动改动）：局外普通目录（如 `%USERPROFILE%\酒馆PR`，因人而异，一律通过"库设置"注册）
- 两个酒馆项目（接入目标，见 `docs/st-sync-feasibility.md`；安装位置因机器而异，由 `TavernDetector` 探测）：
  - 原版 SillyTavern：数据在 `<安装目录>\data\default-user\`
  - TauriTavern：数据在 `<安装目录>\cache\default-user\`（`default\` 是出厂副本，禁止编辑）

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
| `tests/TavernVault.Core.Tests` | xUnit 单元测试（数量以 `dotnet test` 输出为准） |

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
- `Storage/SettingsStore.cs` — 设置/索引持久化（索引带 `version` 门控，当前版本 4）
- `Storage/BackupStore.cs` — 文件级备份（manifest.json、按文件保留份数、还原前再备份、`RelocateTo` 迁移）
- `FileOps/FileOperations.cs` — 重命名/移动/回收站/路径防护/`GetSaveAsPath` 自动命名

App：
- `App.xaml.cs` — 启动流程：单实例 Mutex → 解析参数 → 起 Kestrel → 窗口模式开 MainWindow（注入令牌）/ 无窗口模式落盘 server-connection.json
- `Hosting/ApiServer.cs` — 全部端点（见 §4）+ 安全中间件（Host 白名单 + 会话令牌）+ `EnsureDefaultRoot` 首次运行默认库
- `Hosting/AppLog.cs` — 滚动日志（数据目录 `logs/`，按日切分保留 7 天，IO 异常全吞）
- `MainWindow.xaml.cs` — WebView2 外壳（`AddScriptToExecuteOnDocumentCreatedAsync` 注入 `window.__TV_TOKEN__`，先于页面脚本）
- `Services/ThumbnailService.cs` — PNG 卡片缩略图缓存（`<数据目录>\thumbs\`，v0.5.1 起随 `--data`；源 mtime+size 旁车失效键）
- `Services/FolderPicker.cs` — 原生文件夹选择框（无窗口模式下禁用）
- `wwwroot/js/main.js` — 入口（主题/启动/设置弹窗/版本号）
- `wwwroot/js/app.js` — 主界面（侧栏类型+**库分组选项卡**/网格/列表/抽屉/备份弹窗）
- `wwwroot/js/editor.js` — 编辑器（角色卡表单+原始JSON、世界书条目、内嵌书、预设可视化三期、原文）
- `wwwroot/js/preset-model.js` — 预设可视化纯函数（分组选择/重排/增删写回，无 DOM，Node 可单测；旁边的 package.json 仅声明 ESM 供 Node 识别）
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
- **索引版本门控**（`SettingsStore.IndexVersion = 4`）：条目模型或识别规则变化时 +1，旧索引直接丢弃全量重建，
  避免增量扫描复用缺少新字段的旧条目（v0.4.0 加 `RootSource` 2→3；v0.6.1 回撤 5 类模板分类时 3→4，
  旧索引里的 kind 数字 8-12 已失效，收藏/标签由扫描快照回填不丢失）。
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
- **默认库**：首次运行 `EnsureDefaultRoot` 探测 `%USERPROFILE%\酒馆PR`，存在才注册为普通库；不存在则保持空库，由前端空态引导添加。酒馆数据目录可通过环境变量 `TV_SILLYTAVERN_DATA` / `TV_TAURITAVERN_DATA` 显式指定。

### 3.8 三逻辑库选项卡（v0.4.1 → v0.4.2）

v0.4.1 引入库根分组（侧栏逐根列出），但接入酒馆后每个酒馆注册 5 个根（characters/worlds/OpenAI Settings/themes/regex），加局外普通根共 11+ 个平铺，且类型计数仍是全局的——文件管理依然混乱。v0.4.2 重构为**三个逻辑库选项卡，互相独立**：

- **逻辑库 = 库根来源的并集**：局外存储（全部 Normal 根）/ SillyTavern（全部 TavernST 根）/ TauriTavern（全部 TavernTT 根）。无「全部资源」总览，默认进入局外存储。
- **`Vault.BuildLibraries()`**（Core 层，可单测）聚合每库的 `total/rootCount/favorites/kinds(8 类含 0)/dirs/tags`：普通库 dirs 按 `RelativeDir` 跨根聚合（root=null），酒馆库 dirs 按注册根逐条列出（含空根 count=0）。`/api/meta` 纯增量加 `libraries` 键；全局 `kinds` 改为三库求和；全局 `roots/userTags` 保留（设置弹窗与移动弹窗依赖含空根的完整根清单）。
- **`QueryParams.Source`**：按来源过滤，与 `RootPath` 同设为 AND。`/api/items?source=` **严格契约**：非 {normal,tavernST,tavernTT} 返回 400，绝不静默当作 Normal。
- **切库重置契约**：切库重置 `kind/dir/root/tag`，保留 `q/fav/sort`。**筛选不持久化**（刷新即重置）；唯一持久化键 `localStorage('tv-library')`，启动时校验 ∈ 三值，非法**立即回写** normal。
- **二级子目录**：酒馆库二级用 `root` 参数（`RelativeDir` 相对各根，characters 根与 worlds 根的顶层文件 RelativeDir 均为 `""`，dir 参数无法区分功能分区）；普通库二级用 `dir` 参数。均与 `source` AND 叠加。
- **空态优先级**：`rootCount===0` → 引导（Normal「添加根目录」/ 酒馆「一键接入」，都开库设置）；`rootCount>0 && total===0` → 建议重扫；有筛选无结果 → 筛选空态。
- **冷升级自愈**：增量复用分支无条件刷新 `RootSource`（LibraryScanner.cs:76）+ 前端 boot 每次启动 rescan（main.js），但 `--server` 冷启动与手改索引不会被覆盖。`Vault` 构造时校验 `RootContaining(FullPath)?.Source != item.RootSource` 即触发一次 Rescan（O(n×roots)），兜住来源漂移。

### 3.9 侧栏手风琴与滚动隔离布局（v0.4.3）

v0.4.3 解决两个体验问题：侧栏三级导航（分类/子目录/我的标签）同时平铺太长，以及"整窗一起滚"的布局 bug。

- **手风琴**：侧栏三个分区改为可折叠手风琴（`#acc-kind/#acc-dir/#acc-tag`），**同一时间最多一个展开**（再点已展开的 = 全部收起）。平滑高度过渡用纯 CSS 实现：`.acc-body { display:grid; grid-template-rows:0fr }`，展开时切 `1fr`（grid 行高过渡比 max-height 干净，无需 JS 测量）；`.acc-inner { overflow:hidden }`。空分区（无子目录/无标签）加 `.disabled` 置灰且不响应点击，而不是隐藏——三分区始终可见。
- **滚动隔离**：`html,body { height:100%; overflow:hidden }` 根除整窗滚动；`#app` 100vh flex 列布局，滚动只发生在 `#sidebar`（overflow-y:auto + min-height:0）与 `#content`（flex:1 + min-height:0 + overflow-y:auto）内部。**flex 子项默认 min-height:auto 会被内容撑破，必须显式 min-height:0** 才能让 overflow 生效——这是整窗滚动 bug 的根因。窗口 resize 时 flex 自动重算，无需 JS。
- **可移植性探测（隐私去硬编码）**：`TavernDetector` 不再硬编码机器路径，改为 `TV_SILLYTAVERN_DATA` / `TV_TAURITAVERN_DATA` 环境变量优先 → `%USERPROFILE%` 约定路径回退（`SillyTavern\data\default-user`、`TauriTavern\cache\default-user`），目录必须含 `characters/` 才认定有效；`EnsureDefaultRoot` 同理只探测 `%USERPROFILE%\酒馆PR`，不存在则保持空库由前端空态引导。仓库与文档内不再出现任何机器特定绝对路径。完整便携方案（首启向导、配置化探测）记入 §11 长远方向。

### 3.10 深度优化（v0.5.0）

v0.5.0 依据第一轮独立架构评审完成五项修复，全部围绕"验证与安全边界补到与设计同等高度"：

- **PNG 另存为数据损坏修复（评审 2.1）**：旧版 `/api/cards/{id}/saveas` 对 PNG 卡片先 `File.Copy` 原图、再用 JSON 文本覆盖、再走 `WriteTexts`——中间那次 `WriteAllTextAsync` 把 PNG 字节流截断成 JSON，产物丢失 IHDR/IDAT/IEND 但签名合法、卡片可读，**静默产出无图像坏卡**。修复 = 删除多余写入，PNG 分支只保留 复制原图 + `CharacterCardFile.Save`（只重嵌 chara/ccv3）。冒烟新增真实 PNG 夹具断言副本含 IDAT 且字节一致，单测新增 `CharacterCardFile_Save_Png_PreservesImageChunks`。
- **本地 API 会话令牌 + Host 白名单（评审 2.2）**：管道最外层中间件校验 `Host ∈ {127.0.0.1, localhost, ::1}`（403 防 DNS rebinding）；`/api/*` 必须携带启动随机生成的令牌——`X-TV-Token` 头或 `?token=` query（img 标签无法带自定义头，两通道保密性等价），不匹配 401（恒定时间比较 `CryptographicOperations.FixedTimeEquals`）。令牌分发：窗口模式 WebView2 `AddScriptToExecuteOnDocumentCreatedAsync` 注入（先于任何页面脚本，对后续导航同样生效）；`--server` 模式写数据目录 `server-connection.json`（url + token，替代旧 server-url.txt）。威胁模型见 README「安全模型」。
- **备份可观测 + 滚动日志（评审 2.3）**：`BackupStore.BackupBeforeWrite/Restore` 增加 `out error` 重载把失败原因带出；`Vault.BackupBeforeWrite` 返回 `string?` 警告文本；写端点响应带 `warnings` 数组，前端保存/还原处 toast 显性提示"本次保存无备份兜底"。`AppLog` 落数据目录 `logs/tavernvault-YYYYMMDD.log`（按日滚动、保留 7 天、异常全吞），`Handle/HandleAsync` 统一记请求错误日志。
- **写路径增量更新 + 原子写（评审 2.5 部分）**：`Vault` 内部 `_byId` 字典（`Find` O(n)→O(1)）；新增 `UpsertItem(fullPath)` / `RemoveItem(fullPath)`，11 处编辑/另存为/还原/删除/重命名/移动端点把全量 `Rescan()`（O(库文件数) 目录枚举 + 持锁串行）替换为 O(1) 单文件更新。**`UpsertItem` 先捕获旧条目收藏/标签再重建后回填**（BuildItem 的 existingById 传空字典不会保留用户数据，实现时必须显式迁移）。`SaveSettings` 对齐 `SaveIndex` 的 tmp+Move 原子写。根级操作（`/api/roots`、tavern/connect）保留全量 Rescan。
- **编辑并发防护 + 单实例（评审 2.5 完整）**：编辑端点（cards/book/lore/text PUT）校验请求体 `expectedModified`（前端保存时带回读取条目的 `modifiedAt`）与文件当前 mtime，差异 >1s 返回 409"文件已被外部修改"——防两个编辑窗口后写覆盖先写；保存响应回传新 `modifiedAt` 供前端更新本地副本。`App.xaml.cs` 命名 Mutex 防双开（两个内存 Vault 共享 index.json 会互相覆盖丢更新）；**v0.5.1 起 Mutex 名掺数据目录哈希**（`ApiServer.ResolveDataDir`），不同数据目录可并存（窗口模式 + `--server` 冒烟），同一目录仍互斥。

数字一致性（评审 2.4）：测试数、版本号不再在文档里写死——测试数以 `dotnet test` / `smoke_api.py` 输出为准，版本号只认 csproj `<Version>`；版本史唯一权威在 README 表格，本文件 §9 是叙述性历程。

### 3.11 安全与可靠性加固（v0.5.1）

v0.5.1 依据 `docs/full-audit-v0.5.0.md`（4 路子审计的全量审查）修复 P0×1 + P1×5 + N1/N2/N3，主题是"**不可信文件内容**与**异常路径的乐观假设**"：

- **预设可视化 XSS（P0-1）**：`editor.js` 生效顺序/未排序两张卡片把第三方预设的 `p.role`（非 system/user/assistant 时回退原值）裸拼进 innerHTML——下载的预设可在 WebView2 内执行脚本并读取 `window.__TV_TOKEN__`，令牌防线对"已在持令牌方内部执行"的攻击无效。修复 = 两处 `escapeHtml(role)`，顺手加固 `mountEditor` 的 `${title}` 与 `main.js` 接入向导的 `${f.source}`。
- **圣域边界两连（P1-5 / P1-6）**：① `LibraryScanner` 的 `AttributesToSkip` 补 `ReparsePoint`——此前 junction 环可让扫描不终止、指向库外的 junction 会把外部文件以合法条目身份纳入可改删范围；② 卡片 `name`（全项目唯一未清洗的内容字段）此前直通内嵌书导出文件路径，`Path.Combine` 吞目录 + `GetSaveAsPath` 无校验，可向库根外写文件。修复 = `Title` 统一 `Clean(…, 200)` + 新增 `FileOperations.SanitizeFileName`（`GetSaveAsPath` 内置清洗，导出端点先清洗再 Combine）。
- **settings.json 损坏防护（P1-1）**：旧版读取异常被静默吞掉 → 空库根 → 启动期"来源漂移自愈"判定任何条目都漂移 → Rescan 用**空索引覆盖 index.json**，收藏/标签永久丢失。修复 = `LoadSettings` 区分"不存在"（默认）与"损坏"（坏文件改名 `.corrupt-*` 保留 + `LoadSettingsWarning` 经日志与 `/api/meta.settingsWarning` 外显）；自愈条件加 `LibraryRoots.Count > 0` 前置；`SaveIndex` 前轮转 `index.bak` 兜底。
- **还原满上限自逐出（N1）**：`BackupStore.Restore` 的安全备份会把本文件份数顶过上限，`PruneLocked` 删掉的"最旧"恰是正在还原的条目 → `File.Copy` 找不到源（v0.3.0 起潜伏，v0.5.0 提速后保存变快更易堆满窗口而暴露）。修复 = 先把源备份读入内存再触发安全备份；写回改 tmp+`File.Replace` 原子落盘。单测 `Restore_Oldest_AtRetentionCap_Succeeds` + 冒烟"满上限还原"回归。
- **备份元数据可靠性**：manifest.json 改 tmp+Move 原子写（崩溃不再截断全部记录）；`List` 按磁盘存在性过滤（运行中服务的内存 manifest 不再把已删文件的幽灵条目暴露给 UI/还原）。
- **配套加固**：`BackupStore.BackupBeforeWrite`/`ThumbnailService.GetAsync` 写前 `CreateDirectory` 自愈（目录被外部清理后不再静默失败）；缩略图失效键改"源 mtime+size 旁车"（还原旧备份后 mtime 回退不再误判新鲜）；`ResolveDataDir` 收敛为单一入口并落绝对路径；单实例 Mutex 按数据目录哈希命名 + 只在持有时 Release；扫描器 tmp 过滤改前缀匹配（`.tmp-xxxxxxxx` 残留不再被索引）。
- **冒烟可重复（N2）**：清理路径修正（此前双层 `testdata-server\testdata-server\backups` 是空操作）+ thumbs 一并清理；**同一数据目录连续多轮冒烟全绿**成为验收标准之一。新增"满上限还原""导出路径逃逸"两个回归段。

### 3.12 可靠性收尾与编辑器重构（v0.5.2）

v0.5.2 依据 `docs/full-audit-v0.5.0.md` 路线图完成备份可靠性、编辑器质量洼地与测试补齐：

- **备份元数据可靠性（P1-2/P1-3）**：`BackupStore.Load` 不再把"磁盘上暂不可见"的记录丢弃（幽灵条目由 List/Restore/Stats 按磁盘过滤，备份目录瞬时不可见不再导致记录被静默清空），缺席时经 `LoadWarning` 告警（与 settingsWarning 同通道外显）；`RelocateTo` 重写为两阶段——阶段一纯复制 + 长度校验（任一失败清理产物、原状态不动、rethrow），阶段二提交（切目录 → **先落新 manifest** → 再删旧文件/旧 manifest），中断后任意点都保持"manifest 与文件一致、旧目录完整可回退"。
- **N4 move 补备份**：移动前对原文件 `BackupBeforeWrite`，响应带 warnings（与 rename 同款）——"先备份 → 写盘"链路对移动不再豁免。
- **角色卡编辑器重构（P1-7/P1-8/P1-10）**：Tab 监听器改 AbortController 生命周期（重建不再累积、旧监听器不再操作已脱离 DOM 的表单并清 dirty——两条静默数据丢失链切断）；保存成功后双视图互刷（raw 与表单都反映最新内容，陈旧视图保存不再回滚保存）；Esc/Ctrl+S 在确认框悬空时让位（`.modal-mask` 检测 + closeEditor 重入保护），Esc 级联死循环修复。连带修复：saveFn 会话残留（加载失败不再误写上一条目）、保存 in-flight 防抖、另存为成功后关闭编辑器（语义明确）、raw 解析失败留在原文视图、世界书搜索框不再标脏、编辑器头部超长文件名省略。
- **N5 409 恢复路径**：保存收到 409 自动重扫索引 + 提示重开；抽屉打开时按 id 重取最新条目（modifiedAt 新鲜）——quick-reference 旧的"重新打开会重扫"指引由"文档说谎"变为事实。
- **App 加固（P2）**：9 个未包裹端点收编 Handle/HandleAsync（异常不再无日志 500）；catch 补 OperationCanceledException；三处裸 catch 改"通用文案 + 完整细节进日志"（不再外泄绝对路径）；Kestrel 请求体上限显式化 21MB；move 越权等不变。WebView2：用户数据目录移至 `%LOCALAPPDATA%\TavernVault\WebView2`（bin 不再被撑大）、NewWindowRequested 仅放行 http/https、NavigationStarting 拦截非本机导航（令牌不再可能注入外部页面）。AppLog 跨天触发一次清理。前端杂项：refreshItems 请求序号防乱序、401 专属提示、tv-view 白名单、boot 失败隐藏 loading、removeRoot 失败 toast。
- **测试补齐（A2/A4/A5/A6/A9）**：新增 `TavernGuardTests`（酒馆源强制备份、TT 保留 10 份、TavernDetector 环境变量探测、RelocateTo 两阶段成败、Load 幽灵告警）；冒烟新增"酒馆护栏"（403/force/强制备份/settings 负向，夹具放 `.smoke/酒馆源` 避免与外层 normal 根嵌套抢注）与"错误合同"两段；删除空壳 Unit1 测试。

### 3.13 v0.5.x 收尾（v0.5.3）

- **UI 清单实跑通过**（外部浏览器 + `?token=` 通道，index.html 内置令牌回退）：主界面/网格/缩略图、预设可视化 + Tab 往返、角色卡表单↔JSON 互切重建（P1-7）、Esc 确认框不重入（P1-10）、放弃修改、保存双视图互刷（P1-8）、世界书搜索不标脏、409 双 toast 自动重扫（N5）、另存为自动关闭、搜索/视图/重扫——12 项全过、0 JS 错误。过程中抓到并修复 index.html 内联脚本语法错误（探针失效即它的症状）。
- **奥卡姆剃刀修剪**（grep 逐项核实无调用方后删除）：`BackupStore.BackupBeforeWrite/Restore` 两个旧签名重载、`PngChunkIO.WriteText` 单键包装（调用方统一走 `WriteTexts`）、`util.js` 的 `debounce`（零引用）、`App.xaml.cs` 两处不可见的 `Console.WriteLine`（WinExe 无控制台）、两份已被本文件与 full-audit 取代的早期评审文档（git 历史可查）。`Vault.AddRoot(string)` 与 `ApiServerHandle` 经核实生产在用，保留。

### 3.14 格式识别与酒馆官方对照（v0.5.2 核查）

对照官方用户数据目录（characters / worlds / OpenAI Settings / TextGeneration Settings / themes / regex / instruct / context / sysprompt / QuickReplies / avatars / backgrounds）逐类核查 `TypeDetector` 与编辑端点。**结论：主干一致，存在三类差异**，已列入 §11 队列（v0.6）：

| 类别 | 官方格式/行为 | TavernVault 现状 | 结论 |
|---|---|---|---|
| 角色卡 | PNG tEXt chara/ccv3；JSON V1/V2/V3 | 一致（内嵌书保形合并） | ✔ |
| 世界书（worlds/） | `entries` 对象（uid 键）、ST 内部格式 | 一致 | ✔ |
| 独立 Spec-V2 世界书 / NovelAI 导出 | `entries` 可为数组；条目 `keys/enabled` | 能识别为 lorebook，但 `GET /api/lore` 仅接受对象（数组返回 400），`PUT` 会把数组容器改写为对象 | **容器不保形**——违反本仓库自订"数组/对象不可互换"约定 |
| 对话预设（OpenAI Settings/） | `prompts` + `prompt_order` | 一致 | ✔ |
| 文本补全预设（TextGeneration Settings/） | 采样参数 JSON（无 prompts） | 未识别 → 落"文本" | 缺类型 |
| 美化主题（themes/） | `main_text_color`、**`italics_text_color`**、**`quote_text_color`**、`blur_tint_color`、`shadow_color`… | 可识别（其余键命中），但 ThemeKeys 含官方不存在的字段名 `italics_color`/`quote_color`；`bogus_folders` 归属待核 | 字段清单需对齐 |
| 正则（regex/） | `scriptName` + `findRegex` | 一致 | ✔ |
| instruct / context / sysprompt 模板、QuickReplies | 各自专用 JSON 结构 | 未识别 → 落"文本" | 缺类型 |
| 头像 / 背景 / 音效等资产 | 图片/音频 | → "其他" | 设计如此（低优先） |

### 3.15 v0.6.0：格式对齐落地 + 新建文件

> **v0.6.1 注**：本节的「格式识别对齐」中，5 类官方模板分类（textgen/instruct/context/sysprompt/quickreplies）
> 已于 v0.6.1 回撤（缺乏编辑价值且个别规则误收预设文件），详见 §3.16；主题键名修正、独立世界书容器保形、
> 新建文件（收敛为 6 类）均保留。

§3.14 的对照结论在本版本落地，外加"新建文件"特性：

- **格式识别对齐**（TypeDetector）：ItemKind 扩至 13 类（新增 textgen/instruct/context/sysprompt/quickreplies，枚举值追加末尾保证旧索引不错位）。识别特征均对照官方源码核实：textgen=`temp`+`rep_pen` 核心或 ≥3 采样键（官方名为 `typical_p`/`mirostat_tau` 等，`typical`/`mirostat_lr` 不存在）；instruct=≥2 个 `*_sequence` 字段；context=`story_string` 单独命中；sysprompt=`{name,content,post_history}` 三键（`post_history` 是与脚本的区分特征，裸 `{name,content}` 仍归脚本）；quickreplies=`qrList`/`quickReplies` 数组。ThemeKeys 修正为官方名（`italics_text_color`/`quote_text_color`/`blur_tint_color`；`bogus_folders` 经核实为官方字段，保留）。`TavernDetector.Subdirs` 扩至 11 项（官方 "TextGen Settings" + 旧约定 "TextGeneration Settings" 双收）。
- **独立世界书容器保形**：`GET /api/lore` 支持 entries 数组容器（Spec V2/NovelAI 导出）——经 CharacterBook Spec→ST 转换返回统一条目 + `raw`，响应附 `container:"object"|"array"`；PUT 按 container 保形合并（数组容器不再被改写为对象）；saveas 同样保形。`CharacterBook.WriteEntries` 顺带强化：未编辑条目（与转换结果一致）直接写回 Raw 原文，实现"字节级不变"（内嵌书流程同样受益——旧实现会给未编辑条目补默认值）。
- **新建文件**：`ContentTemplates`（Core/Templates.cs）为 10 个 JSON 类 + text 提供官方格式骨架，硬验收=模板必须被自家 TypeDetector 识别回原 kind（单测 + 冒烟双层覆盖）；`POST /api/items/create`（重名自动 "(n)" 序号；仅普通库根，酒馆来源 400）；前端 topbar「新建」按钮（仅局外库显示）→ 11 类菜单 → 命名 → 创建后直接进入编辑器。editor.js/app.js 可编辑白名单同步扩至 11 类（新类型走通用原文编辑器）。
- **测试**：单测 100 项（+DetectionTests 20、TemplatesTests 21）；冒烟 191 项（+格式识别对齐、新建文件两段）。UI 目检：新建世界书/文本补全预设全流程通过。

### 3.16 v0.6.1：回撤 5 类官方模板分类 + 首次打包

实践检验后回撤 v0.6.0 的一部分：侧栏的 **文本补全预设 / 指令模板 / 上下文模板 / 系统提示模板 / 快捷回复** 5 个分类
没有存在必要——它们都只能走通用原文编辑器（无专属编辑能力，分类不带来任何功能差异），且个别识别规则
（textgen 的"≥3 采样字段即命中"）会误收带采样器的预设文件。决策原则与 v0.5.3 一致：奥卡姆剃刀。

- **回撤内容**：`ItemKind` 删 5 个枚举值回到 8 类；`TypeDetector` 删 textgen/instruct/context/sysprompt/quickreplies
  五组识别规则（`{name,content,post_history}` 官方 sysprompt 文件与裸 `{name,content}` 同规则归"脚本"，
  其余模板 JSON 回落"文本"）；`ContentTemplates` 新建模板收敛为 6 类（character/lorebook/preset/theme/script/text）；
  `TavernDetector.Subdirs` 回撤至 5 个官方功能分区（characters/worlds/OpenAI Settings/themes/regex）——
  接入向导不再注册模板类目录，其中的 JSON 仍按内容落"文本/脚本"可浏览可编辑。
- **保留内容**（v0.6.0 中经得起检验的部分）：ThemeKeys 官方字段名修正、独立世界书数组容器保形（Spec V2/NovelAI）、
  新建文件机制本身、预设可视化二期编辑能力。
- **索引冷升级**：`IndexVersion` 3→4，旧索引（含失效 kind 数字 8-12）整体作废重建；收藏/标签随扫描快照回填，
  `index.bak` 留档上一版。
- **首次打包**：`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`（含
  `IncludeNativeLibrariesForSelfExtract` / `DebugType=none` / `AllowedReferenceRelatedFileExtensions=none`），
  产物 = 单文件 `TavernVault.exe`（约 153 MB，内嵌 .NET 10 运行时）+ `wwwroot\` 前端目录（AppContext.BaseDirectory
  定位，与 exe 同级分发）；已实跑 `--server` 模式验证 API 与版本号。命令与产物说明见 README「打包」。
- **测试**：单测 90 项（DetectionTests 5 类用例改为回落断言 + 误收回归；TemplatesTests 收敛）；冒烟 168 项
  （识别夹具改断言回落、create 段收敛 6 类、新增"采样字段+prompts 恒判预设"与"kind=textgen 400"两条回归），
  同数据目录连跑两轮全绿。前端 5 个 js `node --check` 通过。

### 3.17 v0.7.0：预设可视化三期 + 学习项目开源定位

- **预设可视化三期**（编辑能力补全，见 §11 立项计划）：
  - **写回逻辑抽纯函数**：新模块 `wwwroot/js/preset-model.js`——`pickGroup`（精确 character_id → 默认 100001 →
    首组）、`reorderGroup`（只重排 order 数组、沿用原对象引用，UI 漏传的项按原次序补回末尾防丢）、
    `addPrompt`（prompts + 当前分组 order 双写，uuid identifier 防冲突）、`removePrompt`（prompts 与**所有**分组
    order 同步移除，`marker/system_prompt` 系统项拒删）。无 DOM 依赖，Node 直接 import 单测
    （`tests/preset-model.test.mjs` 18 项；`wwwroot/js/package.json` 仅声明 ESM 供 Node 识别）。
  - **editor.js 接入**：生效顺序列表加 HTML5 拖拽（dragover 按上下半区计算插入位、drop 写回当前分组）、
    行尾删除按钮（系统项禁用 + 确认弹窗）、工具栏「新增提示词」内联表单（名称/角色/内容，创建后自动选中新行
    进详情编辑）、多分组下拉切换（仅 >1 组时显示；未排序清单随分组联动）。顺带修复既有 bug：
    行模板误写 `o.enable`（正确字段 `enabled`，quick-reference 数据格式坑第 1 条正是它），此前所有行初始都带
    置灰 `.off`。
  - **UI 实跑验收**（IAB 浏览器 + 合成 DragEvent，夹具双分组 6 提示词）：拖拽重排 DOM 次序与插入指示 ✓、
    分组切换联动 ✓、新增（uuid/追加/自动选中/统计更新）✓、删除（确认弹窗/两数组同步消失）✓、
    保存后磁盘文件逐项核对（重排/新增/删除/另一分组未动）✓。
- **列表视图类型徽标纵向成列**：`.kind-chip` 定宽 58px 居中、`.r-meta` 定宽 138px 右对齐 + tabular-nums——
  大小文本宽度差（"10.2 KB"/"824.6 KB"）不再把徽标列顶得左右锯齿。
- **开源定位**：README 声明个人学习项目（项目搭建与管理工程实践）；选定 **MIT** 协议（根目录 `LICENSE`，
  徽章 + 许可证节）——学习项目首选宽松协议，比 Apache-2.0 少专利条款复杂度。
- **测试**：单测 90（无变化）+ preset-model Node 测试 18 项；冒烟 168 × 2 轮全绿；前端 6 个 js `node --check`
  （.mjs 拷贝方式）通过；浏览器 UI 端到端实跑一轮全过。

### 3.18 v0.7.1：真实使用反馈收口——酒馆编辑退役为「导出副本」+ 可视性补课

真实使用实测（ST/TT 各 70+ 资源）推翻了 §3.14 时代"按需读取 = 冷生效够用"的假设（详见
`docs/st-sync-feasibility.md` §六）：

- **实时生效从不存在**（酒馆无文件监视）；**冷生效也不可靠**——角色卡被酒馆服务端常驻内存缓存，
  重新打开对话/重新选择仍可能读旧值，酒馆界面操作还会把内存旧数据**回写覆盖**外部修改。
  用户视角即"保存了但没生效，重进资源管家也看不到改动"（被酒馆覆盖回去了）。TT 的 cache 更新即重置，更糟。

- **就地编辑退役**：`PUT /api/cards`、`/api/cards/{id}/book`、`/api/lore`、`/api/text` 对酒馆来源
  （RootSource≠Normal）一律 403（`TavernEditGuard`，错误文案指引导出）；前端抽屉隐藏编辑/编辑内置书入口，
  editor.js 入口同样拦截。酒馆库转为只读托管：浏览/搜索/收藏/标签/删除（回收站）/备份还原/
  重命名·移动（force）照旧，写前强制备份保留（作用于 rename·force 与还原）。
- **导出副本**：`POST /api/items/{id}/export`——酒馆源专用，字节级 `File.Copy` 到第一个局外库根
  （`GetSaveAsPath` 时间戳命名，重名自动序号；无局外库根 400），返回新 id；前端导出后直接打开副本详情
  （可编辑）。编辑酒馆资源的唯一可靠路径 = 导出副本 → 编辑 → 酒馆自带导入写回（写路径走酒馆自身，
  内存缓存随之更新，无覆盖问题）。`openDrawer` 顺带修掉"缓存未命中即静默返回"（跨库跳转场景：
  导出副本 / 修改历史跳转都不在当前过滤视图里）。
- **修改历史**（用户诉求：改过的文件忘了名只能一个个翻）：`GET /api/history`——备份清单
  （每次应用内写入前必先备份，`BackupStore.All()` 新增）按原文件聚合、最近写入倒序、上限 100，
  行含当前条目 id/kind/来源/写入次数；前端侧栏「修改历史」按钮 → 弹窗点击直达详情。
  边界：酒馆侧的直接改动不经应用，不产生记录。
- **数据目录可视化**（用户反馈"至今不知默认存储位置"）：`/api/meta` 增加 `dataDir`；
  库设置新增「存储位置」节（路径展示 + 一键打开，`/api/reveal` 支持 `{dataDir:true}`）。
- **冒烟事故记录**：曾把 reveal dataDir 的真实调用放进冒烟——本地服务就是用户桌面，
  每轮冒烟真实弹出资源管理器窗口。已改为仅断言 404 合同（零副作用）。
  **教训：冒烟不得包含桌面副作用动作。**
- **测试**：单测 90 + preset-model 18 不变；冒烟 183×2 轮全绿（+15：酒馆 PUT 403 三路、403 不落盘、
  导出副本全链路、局外源导出 400、meta.dataDir、history 聚合/倒序/直达、reveal 404）；
  浏览器 UI 实跑：酒馆抽屉（无编辑 + 导出主按钮）、导出后抽屉切副本可编辑、历史弹窗有数据、
  库设置数据目录行。

### 3.19 v0.7.2：文件监视自动重扫 + 冒烟复审 + 输入框主题修复

§11 复审后优先级第一的 **FileSystemWatcher 自动重扫** 落地，外加用户实测反馈的两处修复：

- **VaultWatcher**（App/Services，v0.7.2）：每个登记库根一个 `FileSystemWatcher`（含子目录、64KB 缓冲），
  事件只做防抖（800ms 合并突发，重扫进行中顺延不并发、不丢事件），到期走一次增量 `Rescan`
  （未变化条目毫秒级复用）。监视是纯读行为，与自身保存/索引写入无回环（数据目录不在库根内）；
  `Error` 事件（缓冲溢出/根不可达）自动重建监视器；`/api/roots` 增删时 `RefreshRoots()` 重建。
  `AppSettings.AutoWatch`（默认 true）控制启停——本版无 UI 开关（网络盘用户有需求再加）。
  ⚠️ 监视对象是**库根**而非数据目录：放进 `<data>` 的文件不会被发现（曾因此误判 watcher 失效）。
- **前端自动刷新**：5s 轮询 `/api/meta`，`lastScanAt` 变化才拉新数据刷新侧栏与列表
  （页面隐藏/弹窗/编辑器打开时跳过，不打断输入）。外部改动到界面可见 ≈ 防抖 0.8s + 重扫 + 轮询，实测 ≤8s。
- **textarea 主题兜底**（用户截图反馈：预设新增条目输入框白底浅字几乎不可见；用户已自行修 `.pd-edit`）：
  根因是裸 textarea 无 background，UA 默认白底叠加主题浅色文字。全局 `textarea` 规则兜底
  （surface-2 底 + 主题字色 + 焦点描边），备用开场白、世界书条目内容框等同类隐患一并解决；
  已自带底色的 `.field textarea`/`.raw-area` 等按优先级自然覆盖。
- **冒烟复审**（用户反馈"测试项有点久了"）：28 段逐段对照当前功能，无测已删功能的残留；
  文件头新增**覆盖范围总览注释**（含永不纳入项：reveal 桌面副作用、前端逻辑归属）；补齐 4 个缺口：
  文件监视自动重扫段（落盘→自动入库/出库，轮询上限 8s）、text saveas 正向、export 未知条目 404、
  history 已删文件过滤。§8 测试体系表同步更新（含 preset-model 层与切库提示）。
- **测试**：单测 90 + preset-model 18 不变；冒烟 **192×2 轮全绿**（+9）；构建 0 警告 0 错误；
  浏览器实测：外部落盘文件在无任何手动操作下 ≤8s 自动出现在网格。

---

## 4. REST API 参考

全部端点定义在 `ApiServer.MapApi`。错误统一 `{ "error": "..." }`，IO/权限类异常由 `Handle/HandleAsync` 包装为 400。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/meta` | 总数、分类计数（三库求和）、用户标签、roots（含来源与 count）、**libraries（三逻辑库聚合：total/rootCount/favorites/kinds/dirs/tags）**、`settingsWarning`（设置损坏告警，正常 null）、版本号 |
| POST | `/api/rescan` | 全量重扫，返回条目数 |
| GET | `/api/items` | 条目查询。参数：`kind,q,tag,fav,sort(name|modified|size|kind),dir,root,source`。**source 非法值 400**，与 root AND |
| GET | `/api/items/{id}` | 单条目 |
| GET | `/api/thumb/{id}` | 角色卡缩略图（JPEG） |
| GET | `/api/image/{id}` | 角色卡原图 PNG（支持 Range） |
| GET/PUT | `/api/cards/{id}` | 角色卡读取/保存。PUT body：`{fields, alternateGreetings, tags}` 增量合并或 `{card}` 整卡替换。**酒馆源 PUT 403**（v0.7.1） |
| POST | `/api/cards/{id}/saveas` | 另存为新文件（PNG 先复制原图再重嵌数据） |
| GET/PUT | `/api/cards/{id}/book` | 内嵌世界书读/写。条目带 `raw` 时保形合并。**酒馆源 PUT 403**（v0.7.1） |
| POST | `/api/cards/{id}/book/saveas` | 内嵌世界书导出为独立世界书 |
| GET/PUT | `/api/lore/{id}` | 世界书读/写（整体重建 entries，其它顶层键保留）。**酒馆源 PUT 403**（v0.7.1） |
| POST | `/api/lore/{id}/saveas` | 世界书另存为 |
| GET/PUT | `/api/text/{id}` | 文本/原始 JSON 读/写（.json 保存前校验）。**酒馆源 PUT 403**（v0.7.1） |
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
| POST | `/api/items/{id}/export` | **导出副本（v0.7.1）**：酒馆源字节级复制到第一个局外库根，返回 `{ok,id,fileName}`；局外源 400、无局外库根 400 |
| GET | `/api/history` | **修改历史（v0.7.1）**：应用内改过的文件按最近写入倒序（备份清单聚合，上限 100），行含 `{id,fileName,kind,kindLabel,rootSource,lastModified,edits}` |
| POST | `/api/reveal` | 资源管理器中显示文件；`{dataDir:true}` 打开数据目录（v0.7.1）。⚠️ 有桌面副作用 |
| POST | `/api/roots` | 添加库根 `{path, source}` |
| DELETE | `/api/roots` | 移除库根 `{path}`（不动文件本身） |
| POST | `/api/tavern/detect` | 检测本机酒馆安装与可接入子目录 |
| POST | `/api/tavern/connect` | 按来源批量注册酒馆子目录为库根 |
| POST | `/api/items/create` | 新建文件 `{kind,name,root?}`。仅普通库根（酒馆来源 400）；kind 限 6 个可模板化类型（character/lorebook/preset/theme/script/text），archive/other 与未知键 400；重名自动 "(n)" 序号 |
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
- **库根（settings.json）**：`LibraryRoots` 为对象数组 `[{"Path":"...","Source":0|1|2}]`（0=Normal、1=TavernST、2=TauriTavern）。`LibraryRootConverter` 反序列化时兼容旧版纯字符串数组（自动按 Normal 处理）。索引版本已升到 4（v0.6.1 回撤 5 类模板分类），旧索引会丢弃重建。
- **酒馆护栏**：`rootSource != 0` 的条目默认禁止重命名/移动（API 返回 403），请求体带 `force:true` 才放行；酒馆源文件写前强制备份（忽略自动备份开关），TT 源备份保留 10 份。

---

## 6. 数据目录与持久化

数据目录默认 `%APPDATA%\TavernVault\`，可用 `--data=<目录>` 覆盖（测试用）：

```
%APPDATA%\TavernVault\
├─ settings.json    # AppSettings：LibraryRoots / UiTheme / AutoBackup / MaxBackupsPerFile / BackupRootPath
├─ index.json       # 索引：{ version: 4, items: [LibraryItem...] }，版本不符则丢弃重建
├─ index.bak        # 上一版索引留档（每次 SaveIndex 前轮转，v0.5.1）
├─ settings.json.corrupt-*  # 设置损坏时的坏文件留档（v0.5.1，正常不存在）
├─ backups\         # 默认备份目录（可自定义到任意位置）
│  ├─ manifest.json # 全部备份元数据
│  └─ <源文件名>\   # 每个源文件一个子目录存放各版本备份
├─ thumbs\          # 角色卡缩略图缓存（可整体删除，会自动重建；随数据目录，v0.5.1 起 --data 生效）
├─ logs\            # AppLog 滚动日志 tavernvault-YYYYMMDD.log（保留 7 天，v0.5.0）
└─ server-connection.json  # 仅 --server 模式：{ url, token }，供脚本读取（v0.5.0，替代 server-url.txt）
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
# 打包分发（v0.6.1 起）——自包含单文件，产物 exe+wwwroot 落 dist/，实跑验证方式见 §3.16
dotnet publish src/TavernVault.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none -o dist/TavernVault-win-x64
```

命令行参数（`App.xaml.cs` → `ApiServer.Build`）：

| 参数 | 作用 |
|---|---|
| `--server` | 无窗口模式；连接信息（url+token）写入数据目录 server-connection.json |
| `--port=<n>` | 指定端口；缺省时随机分配（`127.0.0.1:0`） |
| `--token=<串>` | 显式指定 API 会话令牌（≥16 字符无空白）；缺省随机生成（v0.5.0） |
| `--data=<目录>` | 覆盖数据目录。⚠️ Git Bash 下绝对路径反斜杠会被转义吞掉，**用相对路径或正斜杠** |

版本号来自 csproj `<Version>`（**当前唯一事实源，见 csproj**），经 `/api/meta.version` 显示在界面左下角。

### 构建/运行铁律（踩过的坑）

1. **先杀进程再构建**：`taskkill -IM TavernVault.exe -F`（Git Bash 写法；cmd 为 `taskkill /IM TavernVault.exe /F`）。运行中的 exe 会锁 DLL，导致构建"成功"但产物是旧的（曾因此误判为前端 bug）。
2. **`dotnet build` 增量构建偶发不拷贝 wwwroot**。已在 `TavernVault.App.csproj` 加 `CopyFrontendFiles` Target（AfterTargets=Build，按时间戳强制同步）。若仍怀疑前端没更新，直接对比 `bin/.../wwwroot` 与 `src/.../wwwroot` 的时间戳/大小。
3. **仓库体积会悄悄增长**：窗口模式运行时 WebView2 把浏览器缓存写在 `bin/.../TavernVault.exe.WebView2\`（曾积累 69M）。该目录可随时整体删除（自动重建）；`--server` 无窗口模式不产生。
4. **前端语法校验必须用 `.mjs`**：`node --check x.js` 按 script 模式，抓不到模块级错误（如非 async 函数里用 await、重复 import）。正确做法：`cp x.js /tmp/x.mjs && node --check /tmp/x.mjs`。
5. **GitHub 直连失败**（国内网络）：本仓库已配置 `git config http.proxy http://127.0.0.1:7890`（用户系统代理）。推送前若报 `Failed to connect to github.com`，确认代理在监听 7890。

---

## 8. 测试体系

| 层级 | 命令 / 入口 | 说明 |
|---|---|---|
| 单元测试 | `dotnet test TavernVault.slnx -c Release` | **数量以输出为准**。覆盖：PNG 块、内嵌书映射、备份/另存为/自定义备份位置、备份失败告警、UpsertItem 用户数据保留、PNG 保存图像块保留、增量扫描、用户数据迁移、来源过滤与 BuildLibraries 聚合（含冷升级自愈） |
| 纯函数测试 | `node tests/preset-model.test.mjs` | 预设可视化写回逻辑（重排/增删/分组，18 项，无框架，退出码即结果） |
| API 冒烟 | `python tests/smoke_api.py` | **数量以输出为准**。夹具自足（自建 testdata 并注册、自清理上轮残留含 backups/thumbs）；连接信息自动读 `<data>/server-connection.json`（TV_CONN/TV_BASE/TV_TOKEN 可覆写）；先 `--server --port=47999 --data=<临时目录>` 再跑；**写操作只作用于 testdata**；**同一数据目录可连续多轮运行全绿**（v0.5.1 起）。含 PNG 完整性回归、满上限还原回归、导出路径逃逸回归、409 并发防护、401/403 安全负向用例、酒馆托管 403 矩阵与导出流（v0.7.1）、文件监视自动重扫（v0.7.2）。**文件头有覆盖范围总览注释**（v0.7.2 复审：28 段与当前功能一一对应，无测已删功能的残留；缺口补齐：text saveas 正向、export 404、history 已删过滤）。**铁律：不含桌面副作用动作**（reveal 会弹资源管理器窗口，只测 404 合同）；前端逻辑不在冒烟范围 |
| UI 冒烟 | 浏览器打开 `http://127.0.0.1:47999/?token=<连接文件里的token>`（v0.5.3 起支持 query 回退） | 页面加载失败时读 `window.__errs`（index.html 内置探针）；截图存 `ui-shots/`（已 gitignore）。注意页面会记住上次停留的库选项卡（切库后再断言） |
| 真实库验证 | `GET` 任意端点 | 只读核对可以，**绝不对真实库 PUT/POST** |

单元测试文件：CharacterBookTests、CardAndDetectionTests、VaultQueryTests、BackupAndSaveAsTests、PngChunkIOTests、ScannerAndFileOpsTests、UnitTest1（数量随版本增长，以命令输出为准）。

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
| v0.4.2 | **三逻辑库选项卡** | 侧栏重构为三个独立逻辑库（局外存储/SillyTavern/TauriTavern，来源并集）；每库独立类型计数 + 二级子目录导航（酒馆库按功能分区根）；`Vault.BuildLibraries` 聚合 + `QueryParams.Source` 过滤（非法 source 400）；移动弹窗按来源分组；构造时来源漂移自愈；单测 36→41、冒烟 49→61（**夹具自足**） |
| v0.4.3 | **手风琴 + 滚动隔离 + 可移植性** | 侧栏分类/子目录/标签三分区手风琴（单开互斥、0fr→1fr 平滑过渡、空分区置灰）；整窗滚动 bug 修复（html/body overflow:hidden，滚动收敛到侧栏与内容区内部，flex min-height:0 链）；`TavernDetector`/`EnsureDefaultRoot` 去硬编码（环境变量 + %USERPROFILE% 约定探测）；全仓库文档脱敏（零机器特定路径）；冒烟脚本路径改用 `TESTDATA` 变量；**fix-1** 修复弹窗超高无法滚动（`.modal` max-height + overflow-y）、版本号改四段式显示 `vX.Y.Z fix-N`、确立版本号规范（见 quick-reference） |
| v0.5.0 | **深度优化（依据第一轮独立架构评审）** | 修复 PNG 另存为静默数据损坏（删多余 WriteAllTextAsync + PNG 夹具回归）；本地 API 会话令牌（X-TV-Token / ?token=，恒定时间比对）+ Host 白名单 + server-connection.json；备份失败显性告警（warnings 外显 + UI toast）+ AppLog 滚动日志；写路径增量更新（`_byId` 字典 + `UpsertItem`/`RemoveItem` 替换 11 处全量 Rescan，**UpsertItem 回填收藏/标签**）+ SaveSettings 原子写；编辑并发防护（expectedModified→409）+ 单实例 Mutex；文档数字收敛（测试数/版本号单一事实源） |
| v0.5.1 | **安全与可靠性加固（依据 docs/full-audit-v0.5.0.md）** | 详见 §3.11。P0：预设可视化 XSS（role 未转义）。P1：扫描跳过 junction、内嵌书导出文件名清洗、settings.json 损坏防护（+index.bak）、还原满上限自逐出（原子写回）、缩略图随数据目录。N1/N2/N3 既有项收尾；冒烟同目录可重复成为验收标准 |
| v0.6.0 | **格式对齐 + 新建文件（新功能迭代）** | 详见 §3.15。ItemKind 13 类（5 个官方新类型 + ThemeKeys 修正 + Subdirs 11 项）；独立世界书容器保形（Spec V2/NovelAI 数组格式读改写不再损坏）；新建文件（11 类官方模板 + create 端点 + topbar 入口，创建即编辑）；单测 100、冒烟 191 |
| v0.6.1 | **分类回撤 + 首次打包** | 详见 §3.16。侧栏 5 类官方模板分类回撤（奥卡姆剃刀：无专属编辑能力 + textgen 规则误收预设文件），文件回落"文本/脚本"、新建模板收敛 6 类、酒馆接入目录回撤 5 分区；索引 3→4 冷升级（收藏/标签快照回填）；自包含单文件打包（153MB，实跑验证）；单测 90、冒烟 168×2 轮 |
| v0.7.0 | **预设可视化三期 + 开源定位** | 详见 §3.17。拖拽排序/新增·删除提示词/角色分组切换（写回抽 preset-model.js 纯函数 + Node 测试 18 项）；修复生效顺序行 `o.enable` 既有置灰 bug；列表视图类型徽标纵向成列；README 声明学习项目 + MIT 协议；浏览器 UI 端到端实跑（合成拖拽 + 落盘核对） |
| v0.7.1 | **真实使用反馈收口** | 详见 §3.18。实测酒馆不实时读外部修改且冷生效会被内存缓存回写覆盖——酒馆来源就地编辑退役（PUT 403），改「导出副本到局外存储」一键流；新增侧栏「修改历史」（备份清单聚合）与库设置「数据目录」可视化；openDrawer 跨库缓存未命中修复；未完成路线必要性全面复审（断链提示/白名单砍掉，FWatcher 上调）；冒烟 183×2 |
| v0.5.3 | **v0.5.x 收尾** | UI 清单实跑 12 项全过（index.html 加 `?token=` 回退供外部浏览器冒烟；顺带修复内联脚本语法错误）；奥卡姆剃刀修剪无用代码（2 个旧重载、WriteText 包装、debounce、Console.WriteLine、2 份被取代的评审文档） |
| v0.5.2 | **可靠性收尾 + 编辑器重构 + 测试补齐（full-audit §8 路线）** | 详见 §3.12。备份：Load 保留幽灵记录 + LoadWarning、RelocateTo 两阶段迁移；move 补写前备份（N4）；编辑器：Tab AbortController + 保存双视图互刷 + Esc 栈顶让位（两条静默数据丢失链切断）+ 409 自动重扫（N5）；App：9 端点异常收编、请求体上限 21MB、WebView2 UDF 搬家 + 导航拦截；测试：TavernGuardTests 6 项 + 冒烟酒馆护栏/错误合同两段，删除 Unit1 空壳 |

### 9.2 当前状态（截至 2026-09-05）

- 分支 `qoder/TavernVault`；v0.7.0 已推送（`e68b463`），v0.7.1 开发完成（真实使用反馈收口）。
- **项目已进入真实使用阶段**（用户自用 ST/TT 各 70+ 资源）：需求与优先级以真实使用反馈为准。
- 验证情况：Release 构建 0 警告 0 错误；单测 90/90 + preset-model Node 测试 18/18；冒烟 **183 项 × 2 轮全绿**；
  前端 6 个 js `node --check` 通过；浏览器 UI 实跑（酒馆抽屉导出流/历史弹窗/数据目录）全过。
- 已知边界（实测）：酒馆侧直接改动不会出现在修改历史里（历史=应用内写入的备份清单）；
  外部改动需手动重新扫描（FWatcher 已上调为下一迭代首选）。

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

## 11. 未完成与未来开发方向（2026-09 必要性复审）

> 复审背景：项目进入**真实使用阶段**（用户自用 ST/TT 各 70+ 资源，v0.7.1 反馈收口）。
> 逐项裁决四档：**保留**（近期做）/ **上调** / **推迟**（记录备查，不做承诺）/ **砍掉**（场景消失）。

### 本轮复审直接落地的反馈（v0.7.1 完成，见 §3.18）

- [x] **修改历史**：改过的文件忘了名只能一个个翻 → 侧栏「修改历史」一键可查（备份清单聚合）。
- [x] **数据目录可视化**：默认存储位置至今不知 → 库设置显示路径 + 一键打开。
- [x] ~~酒馆接入增强（断链提示 / 子目录白名单）~~ → **砍掉**：就地编辑退役后酒馆库只读+导出，
      重命名本就默认 403，断链风险场景消失；白名单随模板目录回撤失去意义。

### 保留（按优先级）

1. **收纳入库【新增，v0.7.3 首选；真实使用反馈】**——"其它机器的文件夹未必井井有条"，需要**自建库 + 收集整理**能力。
   现状：可登记任意文件夹为库根、单文件"移动到…"、新建空白文件，但**没有批量收集与归类**。开发计划：
   - 后端 `POST /api/collect`（`{source, root, move?}`）：预扫描来源文件夹（复用 TypeDetector 内容识别，递归）
     → 返回分类预览（各类型数量 + 文件清单 + 建议跳过项）；确认后执行——默认**复制**（源目录不动）进
     目标局外库根的**类型子目录**（角色卡/世界书/预设/美化/脚本/文本，与酒馆功能分区命名对齐），
     重名自动 "(n)" 序号；返回报告（成功/跳过/失败清单）。
   - 前端「收纳入库」入口（库设置 + 空库空态引导）：选来源目录 → 分类预览（可取消勾选）→ 执行 → 报告。
   - 与「可移植性」联动：首启向导的"一键创建我的库"即本特性特例（建库根 + 引导收纳）。
   - 验收：冒烟（混合散乱夹具 → collect → 分类落位 + 源未动）+ 单测（分类映射/重名/路径防护）。
2. **API 集成测试进 `dotnet test` + CI**：TestServer 收编冒烟，摆脱"手工起服务再跑脚本"两步走；
   开源前的工程底线。
3. **内嵌世界书合入**：独立世界书导入卡片内嵌书（导出已支持）。纯局外操作，不受酒馆问题影响，
   用户价值明确。
4. **可移植性**（v0.4.3 起步）：首次启动向导（无库根时引导创建/选择库目录 + 探测酒馆）、可选便携模式——
   与「收纳入库」联动（见上）。
5. **前端 editor.js 拆分 + UI 自动化安全网**：god-file 持续膨胀（每期编辑器迭代都在加重）；
   先落 UI 自动化冒烟再拆；编辑器 dirty/saveFn 会话化（full-audit P1-7 深修项）。

### 推迟（记录备查，不做承诺）

- **应用图标设计**（v0.7.2 应用户要求入队）：当前 exe/窗口为默认图标、网页 favicon 是 🏺 emoji 占位。
  需统一设计一套：瓶/馆意象、深浅底皆可辨识；产出多尺寸 `.ico`（WPF 窗口 + exe）+ SVG favicon + README 配图。
- **备份健康度**（上次成功备份时间/目录可写性探测）——修改历史已覆盖"改了什么"的主要可视性诉求，
  余下增量价值小。
- **重复资源检测**（内容指纹找重复副本）。
- **内容指纹追踪用户数据**（外部改名后收藏/标签仍能找回，替代纯路径哈希 Id）。
- **形态扩展**：Core 层复用做 CLI 或托盘工具。
- **AutoWatch UI 开关**：文件监视目前默认常开（settings.json 可改 `AutoWatch:false`）；网络盘用户若遇
  watch 异常再加设置界面开关。

### 已完成（历史存档）

- [x] **文件监视自动重扫**（v0.7.2，复审后优先级第一，见 §3.19）：外部/酒馆侧改动免手动重扫。
- [x] 修改历史 + 数据目录可视化（v0.7.1，见 §3.18）
- [x] 浏览器 UI 清单跑一轮（v0.5.3，12 项全过，见 §3.13）
- [x] 发布/分发打包（v0.6.1，自包含单文件，见 §3.16）
- [x] 预设可视化三期（v0.7.0，见 §3.17）；格式识别对齐（v0.6.0 落地 / v0.6.1 收敛，见 §3.15-3.16）；
      新建文件（v0.6.0 上线 / v0.6.1 收敛 6 类）

---

## 12. 接手开发 Checklist

- [ ] 读本文档 + `README.md` + `docs/quick-reference.md`（速查）+ `docs/st-sync-feasibility.md`（酒馆背景）
- [ ] `taskkill` 旧进程 → `dotnet build TavernVault.slnx -c Release` → 确认 bin/wwwroot 时间戳最新
- [ ] 前端改动后用 `.mjs` 方式 `node --check` 全部 5 个 js（api/app/editor/main/util）
- [ ] 改动 Core 后跑 `dotnet test`；改动 API 后跑 `smoke_api.py`（临时 data 目录，**注意 Git Bash 下 `--data` 用相对路径**）
- [ ] UI 改动用浏览器截图核对，读 `window.__errs` 确认无模块错误
- [ ] 提交前 `git status` 确认分支（`qoder/TavernVault`）；推送走代理 7890
- [ ] 任何写操作只在 `testdata/` 临时目录验证，真实库只读
- [ ] 改动条目模型（`LibraryItem`）记得 `IndexVersion` +1，否则旧索引增量复用会缺新字段

---

**文档版本**：4.4 · **最后更新**：2026-09-05 · 对应程序版本 v0.7.2
