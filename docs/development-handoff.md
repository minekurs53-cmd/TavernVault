# TavernVault 开发交接文档

> 目的：让任何新接手的 agent / 开发者在不读完整会话历史的情况下，能立即理解项目、继续开发、避开已知坑。
> 最后更新：v0.3.1（含未随提交发布的预设可视化二期）。

---

## 1. 项目定位

为 SillyTavern（酒馆）玩家做的 **Windows 桌面资源管理器**：把散落在文件夹里的角色卡 / 世界书 / 预设 / 美化主题 / 脚本集中索引、检索、编辑、整理，并（规划中）与本机部署的酒馆项目双向同步。

- 源码：`D:\agent\TavernVault`
- 用户真实资源库（只读扫描，绝不主动改动）：`D:\agent\酒馆PR`
- 两个酒馆项目（接入目标，见 `docs/st-sync-feasibility.md`）：
  - 原版 SillyTavern：`D:\agent\SillyTavern`，数据在 `data\default-user\`
  - TauriTavern：`D:\agent\TauriTavern`，数据在 `cache\default-user\`（`default\` 是出厂副本，禁止编辑）

---

## 2. 技术栈与架构

- **.NET 10**（SDK 10.0.301）+ **ASP.NET Core (Kestrel)** 本地 REST API，只监听 `127.0.0.1`
- **WPF + WebView2** 桌面外壳（Win11 自带运行时）；`--server` 模式可无窗口运行
- **原生 HTML/CSS/JS 前端**（无 npm、无构建链），静态托管于 `wwwroot`
- **System.Text.Json (JsonNode)** 做无损 JSON 编辑（保留未知字段）

三个工程（`TavernVault.slnx`）：

| 工程 | 职责 |
|---|---|
| `src/TavernVault.Core` | 无 UI 依赖的核心库：PNG 数据块、角色卡/内嵌书读写、类型识别、扫描索引、设置/索引/备份持久化、文件操作。可单测 |
| `src/TavernVault.App` | WPF 外壳 + `Hosting/ApiServer.cs`（全部 REST 端点）+ `wwwroot` 前端 |
| `tests/TavernVault.Core.Tests` | xUnit 单元测试（当前 34 项） |

### 关键文件索引

Core：
- `Cards/PngChunkIO.cs` — PNG 分块读写（tEXt 替换/插入、CRC 重算、`WriteTexts` 单次重写多块）
- `Cards/CharacterCardFile.cs` — 角色卡加载/保存（PNG 内嵌 chara+ccv3；JSON 根级 V1 镜像同步 `SyncLegacyMirror`）
- `Cards/CharacterBook.cs` — 内嵌世界书 Spec V2 ↔ ST 内部格式双向映射（`Raw` 原样保留未编辑字段）
- `Detection/TypeDetector.cs` — 基于内容的类型识别
- `Scanning/LibraryScanner.cs` — 递归扫描 + **增量复用**（路径+大小+修改时间不变则复用旧条目）+ 点目录过滤
- `Storage/Vault.cs` — 内存索引 + 查询 + 用户数据快照迁移 + `BackupBeforeWrite`
- `Storage/SettingsStore.cs` — 设置/索引持久化（索引带 `version` 门控）+ `BackupStore` 同目录
- `Storage/BackupStore.cs` — 文件级备份（manifest.json、按文件保留份数、还原前再备份）
- `FileOps/FileOperations.cs` — 重命名/移动/回收站/路径防护/`GetSaveAsPath` 自动命名

App：
- `Hosting/ApiServer.cs` — 全部端点（items/cards/book/lore/text/saveas/backups/settings/roots/…）
- `wwwroot/js/app.js` — 主界面（侧栏/网格/列表/抽屉/备份弹窗）
- `wwwroot/js/editor.js` — 编辑器（角色卡表单+原始JSON、世界书条目、内嵌书、**预设可视化二期**、原文）
- `wwwroot/js/main.js` — 入口（主题/启动/设置弹窗/版本号）
- `wwwroot/js/api.js` — fetch 封装（`get/post/put/del` 独立导出 + `api` 对象）

---

## 3. 运行与构建

```bash
dotnet build -c Release
# 窗口模式
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe
# 无窗口（调试/测试）
./.../TavernVault.exe --server --port=47999 [--data=<目录>]
```

- 数据目录（默认）：`%APPDATA%\TavernVault\`（settings.json / index.json / backups/ / thumbs/）
- `--data=` 可覆盖数据目录（测试用，避免污染真实配置）
- 版本号来自 csproj `<Version>`（当前 0.3.1），经 `/api/meta.version` 显示在左下角

### 构建/运行铁律（踩过的坑）

1. **先 `taskkill //IM TavernVault.exe //F` 再 `dotnet build`**。运行中的 exe 会锁 DLL，导致构建"成功"但产物是旧的（曾因此误判为前端 bug）。
2. **`dotnet build` 增量构建偶发不拷贝 wwwroot**。已在 `TavernVault.App.csproj` 加 `CopyFrontendFiles` Target（AfterTargets=Build，按时间戳强制同步）。若仍怀疑前端没更新，直接对比 `bin/.../wwwroot` 与 `src/.../wwwroot` 的时间戳/大小。
3. **前端语法校验必须用 `.mjs`**：`node --check x.js` 按 script 模式，抓不到模块级错误（如非 async 函数里用 await、重复 import）。正确做法：`cp x.js /tmp/x.mjs && node --check /tmp/x.mjs`。
4. **GitHub 直连失败**（国内网络）：本仓库已配置 `git config http.proxy http://127.0.0.1:(端口)`（用户系统代理）。推送前若报 `Failed to connect to github.com`，确认代理在监听 (端口)。

---

## 4. 数据格式要点（接手编辑逻辑必读）

- **角色卡**：PNG 内嵌 `tEXt` 块 `chara`/`ccv3`（base64 JSON，V2/V3）；或 JSON（V2 `spec+data`，或 V1 平铺，或 ST 导出带根级镜像字段）。编辑 `data`，保存时 PNG 一次重写两块；JSON 同步根级镜像。
- **内嵌世界书**：`data.character_book.entries`。两种条目格式：
  - Spec V2：`keys/secondary_keys/enabled/insertion_order/position("before_char"|"after_char")/extensions`
  - ST 内部：`key/keysecondary/disable/order/position(0-6)/depth/probability`
  - `CharacterBook` 读取时统一转 ST 格式并把 Spec 原条目放 `Raw`；写回时 Spec 条目只合并被编辑字段，`Raw` 里的 `id/selective/use_regex/extensions` 等原样保留。**容器形态（数组/对象）不变。**
- **预设**：`prompts[]` + `prompt_order[]`。⚠️ **`prompt_order[i].order[j]` 的启用字段是 `enabled`，不是 `enable`**（真实 ST 文件实测）。`prompts[j].system_prompt===true` 表示系统管理项（内容只读）。
- **世界书**：`entries` 为对象（键=索引）或数组；ST 格式字段 `key/keysecondary/content/comment/constant/disable/order/position/depth/probability`。

---

## 5. 已实现功能（截至当前工作区）

- v0.1.0：扫描/分类/搜索/收藏/标签/重命名/移动/回收站/角色卡表单+原始JSON/世界书条目/原文编辑器/深浅色/网格列表
- v0.2.0：内嵌世界书识别+编辑、重命名移动用户数据迁移、JSON 根级镜像同步、增量扫描、索引版本门控、Esc 级联修复等
- v0.3.0：另存为（自动命名）、备份与还原、预设可视化一期（只读）、`docs/st-sync-feasibility.md`
- **当前未提交（v0.3.1 工作区）**：
  - 库设置修复（`api.get/post` 误用 → 改用独立导出的 `get/post`；此前导致设置弹窗按钮全部失效、遮罩卡死）
  - Esc 兜底关闭任意弹窗
  - 左下角版本号（`#app-version`，在"上次扫描"上方）
  - **预设可视化二期**：采样参数可编辑（布尔=勾选、数字=校验、字符串=文本）、生效顺序 `enabled` 勾选开关、未排序列表、提示词详情可编辑（名称/内容/注入位置/深度/禁止覆盖）、可视化↔原文双向同步、保存/另存为统一走 `currentText()`
  - csproj `CopyFrontendFiles` 确定性拷贝 + `<Version>0.3.1`

---

## 6. Git 状态

- 远程：`origin https://github.com/minekurs53-cmd/TavernVault.git`（私有）
- 已推送提交：`8f2c055`(v0.1.0) → `a1ad1d9`(v0.2.0) → `3e22813`(v0.3.0)
- 当前分支：`qoder/TavernVault`（注意不是 main；推送时确认目标分支）
- 工作区有 6 个未提交修改（csproj/css/index.html/app.js/editor.js/main.js）= 上述 v0.3.1 内容，**待提交**

---

## 7. 测试体系

- 单元测试：`dotnet test -c Release`（34 项，含 PNG 块、内嵌书映射、备份/另存为、增量扫描、用户数据迁移）
- API 冒烟：`tests/smoke_api.py`（49 项）。流程：先 `--server --port=47999 --data=<临时目录>`，`POST /api/roots` 注册临时 `testdata/`，跑完删除。**绝不用真实库做写测试。**
- UI 验证：用浏览器自动化打开 `http://127.0.0.1:47999/`。页面加载失败时读 `window.__errs`（index.html 内置探针）。截图存 `ui-shots/`（已 gitignore）。
- 真实库只读验证：可 `GET` 任意端点核对，但**不要**对真实库 `PUT/POST` 写操作。

---

## 8. 已知问题 / 待办（按优先级）

1. **接入酒馆**（方案 A'，可行性已确认）：接入向导注册两个酒馆数据子目录为带"酒馆源"标记的库根；酒馆源内默认禁止移动/重命名角色卡（ST 对话按文件名引用）；TT cache 目录提高备份份数。
2. **预设三期**：拖拽排序（写 `prompt_order.order` 数组）、新增/删除提示词（系统项防误删）、角色分组切换。
3. 内嵌书 ← 独立世界书 合入（导出已做）。
4. 重复资源检测（内容指纹）。
5. FileSystemWatcher 自动重扫；批量操作（多选移动/打标）。
6. PNG 仅支持 tEXt（zTXt/iTXt 极少见，未写）。
7. 移动/重命名后"我的标签"靠快照迁移已覆盖应用内操作；**外部**文件管理器改动仍会丢（可后续做内容指纹追踪）。

---

## 9. 接手开发 Checklist

- [ ] 读本文档 + `README.md` + `docs/st-sync-feasibility.md`
- [ ] `taskkill` 旧进程 → `dotnet build -c Release` → 确认 bin/wwwroot 时间戳最新
- [ ] 前端改动后用 `.mjs` 方式 `node --check` 全部 5 个 js
- [ ] 改动 Core 后跑 `dotnet test`；改动 API 后跑 `smoke_api.py`（临时 data 目录）
- [ ] UI 改动用浏览器截图核对，读 `window.__errs` 确认无模块错误
- [ ] 提交前 `git status` 确认分支；推送走代理 (端口)
- [ ] 任何写操作只在 `testdata/` 临时目录验证，真实库只读
