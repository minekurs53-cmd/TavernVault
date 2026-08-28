# 酒馆资源管家 (TavernVault)

一个用于集中管理 SillyTavern（酒馆）资源的 Windows 桌面应用：角色卡、世界书、预设、美化主题、脚本等，不再散落在文件夹里翻找。

![技术栈](https://img.shields.io/badge/.NET-10-blue) ![平台](https://img.shields.io/badge/平台-Windows%2010%2F11-lightgrey)

## 功能

- **自动识别分类**：递归扫描库目录，按**文件内容**（而非文件夹名）识别类型——角色卡（PNG 内嵌 chara/ccv3 或 V1/V2/V3 JSON）、世界书（entries 结构）、预设（prompts + 采样器）、美化主题、酒馆助手脚本/正则、文本、压缩包、其他
- **浏览与查找**：网格/列表双视图、关键词搜索（名称/描述/创作者/标签）、排序、分类筛选、收藏
- **程序内编辑**
  - 角色卡：表单编辑全部常用字段（名称/描述/性格/场景/开场白/备用开场白/示例对话/标签/创作者…），保存写回 **PNG 内嵌数据块**（chara + ccv3 同步更新，图像数据与其它块字节级保留）或 JSON 文件；也可直接编辑原始 JSON
  - 世界书：条目列表 + 逐条编辑（关键词/内容/常驻蓝灯/插入位置/深度/概率…），可增删条目
  - 预设 / 美化 / 脚本 / 文本：带 JSON 校验与格式化的原文编辑器
- **文件操作**：重命名、移动（跨库根/自动建目录）、删除（进系统回收站）、打开所在文件夹、复制路径
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
│  ├─ Cards/       # PNG 数据块读写(PngChunkIO) + 角色卡读写(CharacterCardFile)
│  ├─ Detection/   # 基于内容的类型识别
│  ├─ Scanning/    # 库扫描与索引构建
│  ├─ Storage/     # 设置/索引持久化 + 查询(Vault)
│  ├─ FileOps/     # 重命名/移动/回收站/路径防护
│  └─ Models/
├─ src/TavernVault.App/         # WPF(WebView2 外壳) + Kestrel API + wwwroot 前端
│  ├─ Hosting/     # ApiServer.cs —— 全部 REST 端点
│  ├─ Services/    # 缩略图缓存、文件夹选择器
│  └─ wwwroot/     # 原生 HTML/CSS/JS 前端（无构建步骤）
│     └─ js/       # api.js / app.js(主界面) / editor.js(编辑器) / main.js(入口)
└─ tests/
   ├─ TavernVault.Core.Tests/   # xUnit 单元测试（PNG 块读写/识别/扫描/文件操作）
   └─ smoke_api.py              # API 冒烟测试（26 项，需服务运行在 47999）
```

## 技术选型

- **.NET 10 + ASP.NET Core (Kestrel)**：本地 REST API 与静态托管，只监听 127.0.0.1，性能好、零重型依赖
- **WPF + WebView2**：桌面外壳，Win11 系统自带运行时
- **原生 HTML/CSS/JS 前端**：无 npm、无构建链，改完即生效
- **System.Text.Json (JsonNode)**：编辑时保留文件中的未知字段，不破坏 ST 兼容性

## 数据位置

- 设置与索引：`%APPDATA%\TavernVault\settings.json`、`index.json`
- 缩略图缓存：`%APPDATA%\TavernVault\thumbs\`
- 你的资源文件本体永远不会被程序移动或修改，除非主动执行编辑/文件操作；删除一律进回收站

## 已知边界 / 后续方向

- 重命名/移动文件后条目 Id 会变化（收藏与"我的标签"保留，因为重扫描时按新路径重新匹配……注：当前按路径哈希匹配，移动后用户标签会丢失，后续可改为按内容指纹追踪）
- PNG 卡片仅支持 tEXt 形式的内嵌数据（ST 标准形式；zTXt/iTXt 极少见，暂不写）
- 界面文案与格式字段面向 SillyTavern 主流格式；遇到非标准文件会落到"文本/其他"分类，不会出错
- 适合后续扩展的点：`ApiServer.MapApi`（加端点）、`editor.js`（加编辑器）、`TypeDetector`（加类型识别）、Core 层可直接复用做 CLI 或托盘工具

## 测试

```bash
dotnet test -c Release                 # 20 项单元测试
# 冒烟测试（先启动 --server --port=47999，会创建临时测试目录，不动真实资源）
python tests/smoke_api.py
```
