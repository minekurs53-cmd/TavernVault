# 酒馆资源管家 (TavernVault)

集中管理 SillyTavern（酒馆）资源的 Windows 桌面应用：角色卡、世界书、预设、美化主题、脚本——不再散落在文件夹里翻找。

![.NET](https://img.shields.io/badge/.NET-10-blue) ![平台](https://img.shields.io/badge/平台-Windows%2010%2F11-lightgrey) ![协议](https://img.shields.io/badge/协议-MIT-green) [![CI](https://github.com/minekurs53-cmd/TavernVault/actions/workflows/ci.yml/badge.svg)](../../actions/workflows/ci.yml)

> **关于本项目**：这是一个**个人学习项目**，用于练习桌面应用的搭建与管理（架构、迭代、测试、安全、打包分发）。功能随自用需求推进，暂不接受功能需求；仓库计划开放，欢迎交流与借鉴。

## 它解决什么问题

- **散乱 → 有序**：把卡/书/预设扔进一个文件夹，按**文件内容**（而非文件夹名）自动识别分类；已有散乱目录可用「收纳入库」一键整理建库
- **酒馆资源看得见、管得住**：本机 SillyTavern / TauriTavern 的资源目录接入为**只读托管**——实测直接改文件会被酒馆的内存缓存回写覆盖，因此编辑酒馆资源的可靠路径是「导出副本 → 编辑 → 酒馆自带导入写回」
- **改过不迷路**：应用内改过的每个文件都有「修改历史」可查；每次覆盖写入前自动备份，坏了能还原
- **外部改动自动可见**：库目录文件监视自动重扫，酒馆侧的增删改数秒内反映到界面，免手动扫描

## 功能总览

<!-- ═══ 维护约定（新增功能时看这里）═══
     ① 在下方对应小节加一行（一行说清"用户得到了什么"，不加实现细节与版本号括注）
     ② 【版本历程】表加一行主题
     ③ 实现原理与踩坑写 docs/development-handoff.md §3 新小节；速查信息写 docs/quick-reference.md -->

### 资源与库

- **内容识别分类**：递归扫描按内容识别 8 类——角色卡（PNG 内嵌 chara/ccv3 或 V1/V2/V3 JSON）、世界书、预设、美化、脚本、文本、压缩包、其他
- **三逻辑库**：局外存储 / SillyTavern / TauriTavern 独立浏览，各库独立的类型分类与子目录导航
- **收纳入库**：散乱文件夹按内容识别后批量复制进库的类型子目录（源目录不动、可选移动、重名自动序号）
- **酒馆接入**：一键探测本机酒馆，把 characters / worlds / OpenAI Settings / themes / regex 注册为只读托管库根
- **新建文件**：6 类官方格式空白模板，创建即进入编辑器；重名自动序号

### 编辑器

- **角色卡**：表单编辑常用字段，保存写回 PNG 内嵌数据块（chara + ccv3 一次同步、图像字节级保留）或 JSON（同步根级镜像字段）；备用开场白与标签
- **角色卡内嵌世界书**：`data.character_book` 逐条编辑，Spec V2 与 ST 内部格式读取统一转换、写入保形合并
- **世界书**：条目增删改；entries 数组/对象容器保形（NovelAI / Spec V2 导出读写不损坏）
- **预设可视化**：采样参数中文总览、生效顺序拖拽排序、新增/删除提示词（系统项防误删）、角色分组切换、提示词全文与统计
- **通用原文编辑**：美化 / 脚本 / 文本带 JSON 校验与格式化；全部编辑器支持另存为（自动命名、重名序号）

### 自动化与整理

- **文件监视自动重扫**：库目录的新增/修改/删除（含酒馆侧改动）防抖自动入索引，界面数秒自动刷新
- **修改历史**：应用内保存 / 还原 / 重命名 / 移动过的文件按最近写入倒序，点击直达详情
- **文件操作**：重命名、跨库根移动（自动建目录）、删除进系统回收站、资源管理器定位
- **备份与还原**：覆盖写入前自动备份；备份位置可自定义（现有备份自动迁移）；酒馆源强制备份、保留份数更高

### 安全与可靠

- **酒馆只读托管**：酒馆来源禁止就地编辑与重命名/移动（明确确认可 force）；写前强制备份
- **本地服务隔离**：Kestrel 只监听 127.0.0.1；API 需随机会话令牌；Host 头白名单防 DNS rebinding
- **数据透明**：库设置显示数据目录真实路径并可一键打开；设置损坏留档、索引留 .bak
- **我的标签 / 收藏**：随索引持久化，重命名/移动自动迁移

## 快速开始

**方式一（推荐，免装环境）**：从 Releases 下载 `TavernVault-win-x64`（自包含单文件，Win10/11 自带 WebView2 即可运行；也可自行执行下方的 publish 命令生成）。

**方式二（源码构建）**：

```bash
dotnet build TavernVault.slnx -c Release
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe
```

> 打包命令与产物说明见 `docs/quick-reference.md`；无窗口服务模式（调试/脚本）：加 `--server --port=<端口>`。

**首次配置三件事（都在界面里）**：

1. 「库设置」登记资源文件夹；或用「收纳入库」从散乱文件夹一键整理建库
2. 要管理酒馆资源就点「接入酒馆」自动探测注册
3. 要改酒馆里的资源：详情页「导出副本」→ 编辑副本 → 酒馆自带导入写回

## 工作原理

核心链路：`TypeDetector 内容识别 → LibraryScanner 增量索引（未变化条目毫秒级复用）→ JsonNode 无损编辑（未知字段永不丢失）→ 写前备份 → 索引增量更新`。

技术栈：.NET 10 + ASP.NET Core (Kestrel 本地 REST API) + WPF/WebView2 外壳 + 原生 HTML/CSS/JS 前端（无 npm、无构建链，改完即生效）。架构与流程图集见 [`docs/architecture-visualization.md`](docs/architecture-visualization.md)，完整原理与数据格式见 [`docs/development-handoff.md`](docs/development-handoff.md)。

## 测试与质量

```bash
dotnet test TavernVault.slnx -c Release   # 单元 93 + 集成 14（真实 Kestrel + 隔离临时库，永不触碰真实库）
node tests/preset-model.test.mjs          # 预设写回纯函数 18（无框架，退出码即结果）
python tests/smoke_api.py                 # API 冒烟 207（先以 --server 模式起服务，见文件头说明）
```

GitHub Actions 每次 push / PR 自动执行构建与全部测试（[`.github/workflows/ci.yml`](.github/workflows/ci.yml)）。

## 安全模型要点

- 一切文件操作限制在已登记库根内；删除走回收站；覆盖写入前备份、失败显性告警不静默
- 下载的卡/预设视为**不可信内容**：界面插值全量 HTML 转义、文件名派生经清洗（无法写出库根）、扫描跳过 junction/符号链接
- 会话令牌恒定时间比对；单实例 Mutex 按数据目录隔离；请求体上限防自锁
- 完整威胁模型与审计报告：[`docs/full-audit-v0.5.0.md`](docs/full-audit-v0.5.0.md) 与开发文档安全章节

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/development-handoff.md`](docs/development-handoff.md) | **权威技术文档**：架构、核心原理、API 参考、数据格式、开发历程、路线图全清单 |
| [`docs/architecture-visualization.md`](docs/architecture-visualization.md) | 架构与流程图集（Mermaid） |
| [`docs/quick-reference.md`](docs/quick-reference.md) | 开发速查：命令 / API / 数据格式坑 / 故障排查 / **文档维护约定** |
| [`docs/full-audit-v0.5.0.md`](docs/full-audit-v0.5.0.md) | 全面安全与质量审查报告 |
| [`docs/st-sync-feasibility.md`](docs/st-sync-feasibility.md) | 酒馆接入可行性分析（含实测修订：为何只读托管） |
| 项目结构 | 见开发文档 §2「关键文件索引」 |
| 数据位置 | 默认 `%APPDATA%\TavernVault`；库设置内可查看并一键打开 |

## 版本历程

<!-- 维护约定：新版本在此加一行【主题】（≤30 字）；详细内容写 development-handoff.md §9.1 与 §3 新小节，不在此复述 -->

| 版本 | 主题 |
|---|---|
| v0.7.x | 预设可视化三期 → 酒馆只读托管+导出副本 → 自动重扫 → 收纳入库 → 集成测试+CI（明细见开发文档 §9.1） |
| v0.6.x | 格式对齐（5 类模板分类回撤）+ 新建文件 + 自包含打包 |
| v0.5.x | 安全与可靠性加固系列：会话令牌、备份加固、编辑器重构、UI 清单、奥卡姆修剪 |
| v0.4.x | 酒馆接入、多库管理、三逻辑库、手风琴布局 |
| v0.1–v0.3 | 初版 → 内嵌世界书 → 另存为与备份 → 预设可视化一二期的打磨 |

## 路线图

完整的已完成 / 未完成 / 已砍掉清单见 [`docs/development-handoff.md` §11](docs/development-handoff.md)。当前队列：内嵌世界书合入 → 便携模式 → 应用图标 → **开放仓库前的全量隐私审计（v1.0.0 门禁）**。

## 许可证

本项目以 [MIT License](LICENSE) 开源——个人学习项目，可自由使用、修改与再分发，需保留版权与许可声明。
SillyTavern 等第三方工具的名字与数据格式归其各自作者所有；本程序管理的资源文件版权归用户自己。
