# 酒馆资源管家 (TavernVault)

一个用于集中管理 SillyTavern（酒馆）资源的 Windows 桌面应用：角色卡、世界书、预设、美化主题、脚本等，不再散落在文件夹里翻找。

![技术栈](https://img.shields.io/badge/.NET-10-blue) ![平台](https://img.shields.io/badge/平台-Windows%2010%2F11-lightgrey)

## 功能

- **自动识别分类**：递归扫描库目录，按**文件内容**（而非文件夹名）识别类型——角色卡（PNG 内嵌 chara/ccv3 或 V1/V2/V3 JSON）、世界书（entries 结构）、预设（prompts + 采样器）、美化主题、酒馆助手脚本/正则、文本、压缩包、其他
- **三库独立管理**（v0.4.2）：侧栏顶部三个逻辑库选项卡——局外存储 / SillyTavern / TauriTavern（按库根来源自动归并），每个库有自己独立的类型分类与子目录导航，互不混显；接入酒馆后每个酒馆的功能分区（角色/世界书/预设/美化/正则）即其子目录
- **酒馆接入**（v0.4.0）：一键检测本机 SillyTavern / TauriTavern 并把其数据子目录注册为带标记的库根，实现"局外编辑、酒馆内生效"；酒馆源文件默认禁止重命名/移动（聊天按文件名引用），写前强制备份
- **浏览与查找**：网格/列表双视图、关键词搜索（名称/描述/创作者/标签）、排序、分类筛选、收藏
- **程序内编辑**
  - 角色卡：表单编辑全部常用字段（名称/描述/性格/场景/开场白/备用开场白/示例对话/标签/创作者…），保存写回 **PNG 内嵌数据块**（chara + ccv3 一次写入同步更新，图像数据与其它块字节级保留）或 JSON 文件（自动同步 ST 导出格式的根级镜像字段）；也可直接编辑原始 JSON
  - **角色卡内嵌世界书**（data.character_book）：详情页显示"内置世界书 · N 条"徽章，可像独立世界书一样逐条编辑。兼容两种条目格式——Spec V2 标准（keys/enabled/insertion_order）与 ST 内部格式（key/disable/order），读取时统一转换、保存时逐条保形合并，id/selective/use_regex/extensions 等未编辑字段原样保留
  - 世界书：条目列表 + 逐条编辑（关键词/内容/常驻蓝灯/插入位置/深度/概率…），可增删条目
  - 预设：**可视化视图**——采样参数中文总览、按 prompt_order 解析的生效顺序（启用状态/角色/字数）、未排序提示词清单、点击查看提示词全文与统计（二期已支持采样参数/生效顺序/提示词详情编辑）；原文视图仍可直接编辑保存
  - 美化 / 脚本 / 文本：带 JSON 校验与格式化的原文编辑器
- **文件操作**：重命名、移动（跨库根/自动建目录）、删除（进系统回收站）、打开所在文件夹、复制路径
- **另存为**：编辑器内一键把当前内容（含未保存修改）另存为新文件，自动命名 `原名-副本 yyyy-MM-dd_HHmmss`，重名自动加序号；内嵌世界书可一键导出为独立世界书
- **备份与还原**：所有覆盖写入（编辑保存/还原/重命名）前自动备份原文件，默认存 `%APPDATA%\TavernVault\backups`，**可自定义到任意位置**（v0.4.1，现有备份自动迁移过去）；详情页可查看备份列表、一键还原（还原前同样先备份当前）、删除；每文件保留份数与开关在库设置中调整
- **我的标签**：给任意资源打自定义标签（如"常用""待整理"），随索引持久化
- **安全边界**：所有文件操作限制在已登记的库目录内；本地服务只绑定 127.0.0.1

## 运行

前置条件：Windows 10/11（自带 WebView2 运行时）+ [.NET 10 SDK](https://dotnet.microsoft.com/)（仅构建需要）。

```bash
dotnet build -c Release
# 方式一：窗口模式（默认）
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe
# 方式二：无窗口服务模式（调试/远程）
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe --server --port=47999
```

首次启动会自动把 `D:\agent\酒馆PR` 设为默认库（可在"库设置"中增删目录），并自动扫描。

## 项目结构

```
TavernVault/
├─ src/TavernVault.Core/        # 核心类库（无 UI 依赖，可单测）
│  ├─ Cards/       # PNG 数据块读写(PngChunkIO) + 角色卡读写(CharacterCardFile) + 内嵌世界书(CharacterBook)
│  ├─ Detection/   # 基于内容的类型识别(TypeDetector) + 酒馆安装检测(TavernDetector)
│  ├─ Scanning/    # 库扫描与索引构建（增量复用）
│  ├─ Storage/     # 设置/索引持久化(SettingsStore) + 查询(Vault) + 备份存储(BackupStore)
│  ├─ FileOps/     # 重命名/移动/回收站/路径防护
│  └─ Models/      # AppSettings / LibraryItem / LibraryRoot / ItemKind
├─ src/TavernVault.App/         # WPF(WebView2 外壳) + Kestrel API + wwwroot 前端
│  ├─ Hosting/     # ApiServer.cs —— 全部 REST 端点
│  ├─ Services/    # 缩略图缓存、文件夹选择器
│  └─ wwwroot/     # 原生 HTML/CSS/JS 前端（无构建步骤）
│     └─ js/       # api.js / app.js(主界面) / editor.js(编辑器) / main.js(入口) / util.js
└─ tests/
   ├─ TavernVault.Core.Tests/   # xUnit 单元测试（36 项）
   └─ smoke_api.py              # API 冒烟测试（49 项，需服务运行在 47999）
```

## 技术选型

- **.NET 10 + ASP.NET Core (Kestrel)**：本地 REST API 与静态托管，只监听 127.0.0.1，性能好、零重型依赖
- **WPF + WebView2**：桌面外壳，Win11 系统自带运行时
- **原生 HTML/CSS/JS 前端**：无 npm、无构建链，改完即生效
- **System.Text.Json (JsonNode)**：编辑时保留文件中的未知字段，不破坏 ST 兼容性

## 数据位置

- 设置与索引：`%APPDATA%\TavernVault\settings.json`、`index.json`
- 备份：默认 `%APPDATA%\TavernVault\backups\`，可在库设置中改为任意位置（现有备份自动迁移）
- 缩略图缓存：`%APPDATA%\TavernVault\thumbs\`（可整体删除，自动重建）
- 你的资源文件本体永远不会被程序移动或修改，除非主动执行编辑/文件操作；删除一律进回收站

## 版本历程与路线图

| 版本 | 主题 |
|---|---|
| v0.1.0 | 初版：扫描/分类/搜索/编辑/文件操作 |
| v0.2.0 | 内嵌世界书、增量扫描、索引版本门控、用户数据迁移 |
| v0.3.0 | 另存为、备份与还原、预设可视化一期 |
| v0.3.1 | 库设置修复、Esc 兜底、预设可视化二期 |
| v0.4.0 | 酒馆接入：库根来源标记、检测/接入向导、安全护栏 |
| v0.4.1 | 侧栏库分组选项卡、备份位置自定义、项目文档体系 |
| v0.4.2 | 三逻辑库选项卡（每库独立分类+二级子目录）、来源过滤与冷升级自愈 |

后续方向（按优先级）：
1. **预设可视化三期**：拖拽排序（写 `prompt_order.order`）、新增/删除提示词（系统项防误删）、角色分组切换
2. 内嵌世界书合入（从独立世界书导入到卡片；导出已支持）
3. 酒馆接入增强：聊天 → 角色卡反向引用检查（改名前提示断链）、接入子目录白名单可配置
4. 重复资源检测（按内容指纹）
5. FileSystemWatcher 监视目录变化自动重扫；批量操作（多选移动/打标）

已知边界：
- PNG 卡片仅支持 tEXt 形式的内嵌数据（ST 标准形式；zTXt/iTXt 极少见，暂不写）
- 界面文案与格式字段面向 SillyTavern 主流格式；遇到非标准文件会落到"文本/其他"分类，不会出错
- 适合后续扩展的点：`ApiServer.MapApi`（加端点）、`editor.js`（加编辑器）、`TypeDetector`（加类型识别）、Core 层可直接复用做 CLI 或托盘工具

## 测试

```bash
dotnet test TavernVault.slnx -c Release    # 36 项单元测试
# 冒烟测试（先启动 --server --port=47999，会创建临时测试目录，不动真实资源）
python tests/smoke_api.py                  # 49 项
```

## 文档

- `docs/development-handoff.md` —— **项目技术文档**：架构、核心原理、API 参考、数据格式、开发历程与未来方向
- `docs/architecture-visualization.md` —— 架构与流程图集（Mermaid）
- `docs/quick-reference.md` —— 开发速查：命令 / API / 数据格式坑 / 故障排查
- `docs/st-sync-feasibility.md` —— 酒馆接入可行性分析（历史决策依据）
