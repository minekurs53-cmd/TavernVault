# TavernVault 快速参考指南

> 日常开发速查。完整原理见 `docs/development-handoff.md`，图示见 `docs/architecture-visualization.md`。
> 最后更新：2026-09-05 · 对应 v0.7.4

## 一分钟了解

- 本地单用户桌面应用：WPF/WebView2 外壳 + Kestrel（**只听 127.0.0.1**）+ 原生 JS 前端
- 管理酒馆资源（角色卡/世界书/预设/美化/脚本），写操作 = 备份 → 写盘 → 增量更新索引
- 所有 JSON 编辑走 `JsonNode` 无损路径，未知字段永不丢失
- **API 有会话令牌 + Host 白名单**：无令牌请求 401、伪造 Host 403（v0.5.0）
- **下载的卡/预设是不可信内容**（v0.5.1）：前端插值全转义、内容字段参与文件名前必清洗、扫描跳过 junction——内容不能变成脚本、路径或越界索引
- 酒馆来源文件有护栏：默认禁改名/移动（403），写前强制备份；**备份失败显性告警不静默**
- 设置/索引/备份 manifest 全部原子写；settings.json 损坏时保留坏文件 + `index.bak` 兜底，**不再可能被启动自愈清空**

## 常用命令

```bash
# 构建（先杀进程！）
taskkill -IM TavernVault.exe -F          # Git Bash 写法；cmd 用 taskkill /IM TavernVault.exe /F
dotnet build TavernVault.slnx -c Release

# 运行
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe            # 窗口模式
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe --server --port=47999 --data=.smoke/data   # 无窗口

# 测试
dotnet test TavernVault.slnx -c Release    # 单元测试（数量以输出为准）
# 冒烟：先启动 --server（连接信息写入 <data>/server-connection.json），脚本自动读取
# 同一数据目录可连续多轮运行（v0.5.1 起自动清理上轮残留并保证全绿）
./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe --server --port=47999 --data=testdata-server &
PYTHONIOENCODING=utf-8 python tests/smoke_api.py

# 前端语法检查（必须 .mjs，否则抓不到模块级错误）
for f in api app editor main preset-model util; do cp src/TavernVault.App/wwwroot/js/$f.js /tmp/$f.mjs && node --check /tmp/$f.mjs && echo "$f OK"; done
node tests/preset-model.test.mjs           # 预设写回纯函数测试（无框架）
```

⚠️ Git Bash 陷阱：`--data=` 的绝对路径反斜杠会被吞，**用相对路径或正斜杠**。

## 前端模块速查（wwwroot/js/）

| 文件 | 职责 |
|---|---|
| `main.js` | 入口：主题切换、启动加载、设置弹窗（库根管理/接入向导/备份设置）、版本号 |
| `app.js` | 主界面：**三逻辑库选项卡**（局外存储/SillyTavern/TauriTavern，切库重置 kind/dir/root/tag、保留搜索/收藏/排序）、每库类型+子目录二级导航、网格/列表、详情抽屉、备份弹窗 |
| `editor.js` | 编辑器：角色卡表单+原始JSON、世界书/内嵌书条目、预设可视化、原文编辑 |
| `api.js` | fetch 封装：`get/post/put/del` 独立导出 + `api` 对象 |
| `util.js` | 通用工具（格式化、转义等） |

## API 速查

**鉴权（v0.5.0）**：所有 `/api/*` 请求必须带会话令牌——`X-TV-Token` 头（fetch 用）或 `?token=` query（img 标签用）；令牌由启动随机生成，窗口模式经 WebView2 注入，`--server` 模式落盘 `server-connection.json`。缺失/错误 401，伪造 Host 403。编辑保存可带 `expectedModified`（读取时的条目 modifiedAt），文件被外部改动时服务端返回 409 拒绝写入。

### 查询
```
GET /api/meta                          # 总数/分类计数(三库求和)/roots(带source+count)
                                       # libraries: 三逻辑库聚合 {key,label,total,rootCount,
                                       #            favorites,kinds(8类含0),dirs,tags}
GET /api/items?kind=&q=&tag=&fav=&sort=&dir=&root=&source=
                                       # source=normal|tavernST|tavernTT（非法值 400）
                                       # 酒馆库二级导航用 root=，普通库用 dir=（与 source AND）
GET /api/items/{id}
GET /api/categories                    # 按根+目录聚合
```

### 编辑（全部走 备份→写→重扫）
```
GET/PUT /api/cards/{id}                # PUT: {fields,alternateGreetings,tags} 或 {card} 整卡；酒馆源 403
GET/PUT /api/cards/{id}/book           # 内嵌世界书；条目带 raw 时保形合并；酒馆源 403
GET/PUT /api/lore/{id}                 # 世界书
GET/PUT /api/text/{id}                 # 文本；.json 保存前校验；酒馆源 403
```

### 另存为（自动命名 `原名-副本 yyyy-MM-dd_HHmmss`）
```
POST /api/cards/{id}/saveas
POST /api/cards/{id}/book/saveas       # 内嵌书导出为独立世界书
POST /api/lore/{id}/saveas
POST /api/text/{id}/saveas
```

### 备份
```
GET    /api/items/{id}/backups         # 该文件备份列表
POST   /api/backups/{bid}/restore      # 还原（先备份当前）
DELETE /api/backups/{bid}
GET    /api/backups/stats              # count/bytes/dir/defaultDir
POST   /api/settings/backup            # {autoBackup,maxPerFile,backupDir}；空串=恢复默认；必须绝对路径
```

### 文件操作
```
POST /api/items/{id}/favorite          # {fav}
POST /api/items/{id}/tags              # {tags:[...]}
POST /api/items/{id}/rename            # {name,force?}  酒馆源需 force
POST /api/items/{id}/move              # {root,dir,force?} 目标根必须已登记
POST /api/items/{id}/delete            # 进回收站
POST /api/items/{id}/export            # 酒馆源导出副本到第一个局外库根（v0.7.1，局外源 400）
GET  /api/history                      # 修改历史：应用内改过的文件按最近写入倒序（v0.7.1，上限 100）
POST /api/reveal                       # {id} 资源管理器定位；{dataDir:true} 打开数据目录（⚠️ 有桌面副作用，冒烟禁用）
POST /api/collect/preview              # 收纳入库预扫 {source} → 分类分组/文件清单/建议跳过（v0.7.3）
POST /api/collect                      # 收纳执行 {source,root,files?,move?}：目标须局外库根；默认复制源不动
```

### 库根 / 酒馆
```
POST   /api/roots                      # {path, source: normal|tavernST|tavernTT}
DELETE /api/roots                      # {path}
POST   /api/tavern/detect              # 探测本机酒馆
POST   /api/tavern/connect             # {source} 批量接入子目录
POST   /api/items/create               # 新建文件 {kind,name,root?}：仅普通库根，重名加序号
POST   /api/pick-folder                # 原生目录选择框（无窗口模式 400）
```

## 资源类型（ItemKind）

| kind 键 | 中文 | 识别依据 |
|---|---|---|
| `character` | 角色卡 | PNG tEXt 内嵌 chara/ccv3，或 JSON 卡片结构 |
| `lorebook` | 世界书 | `entries` 结构 |
| `preset` | 预设 | `prompts`+采样器（带 prompts 数组恒判预设，采样字段再多也不例外） |
| `theme` | 美化 | 主题 CSS/JSON 特征（官方字段名，v0.6.0 修正） |
| `script` | 脚本 | 酒馆助手脚本/正则特征；`{name,content,post_history}`（官方 sysprompt）也归此 |
| `text` | 文本 | 可读文本扩展名 |
| `archive` | 压缩包 | zip/7z/rar 等 |
| `other` | 其他 | 兜底 |

> v0.6.1 回撤了 v0.6.0 曾设的 5 类（`textgen`/`instruct`/`context`/`sysprompt`/`quickreplies`）：
> 官方模板类 JSON 缺乏编辑价值且个别规则误收预设文件，统一回落"文本/脚本"（原文编辑器仍可编辑）；
> `/api/items/create` 对这 5 个 kind 键返回 400。

## 数据格式坑（改编辑器前必看）

| 坑 | 正确做法 |
|---|---|
| 预设启用字段 | `prompt_order[].order[].enabled`，**不是 `enable`** |
| 系统管理提示词 | `prompts[].system_prompt === true`，内容只读防误删 |
| 预设结构增删/排序 | 一律走 `preset-model.js` 纯函数（prompts 与所有分组 order 双数组一致性、系统项拒删、漏传补回已在其内兜底） |
| 内嵌书条目双格式 | Spec V2(`keys/enabled/insertion_order`) ↔ ST(`key/disable/order/position 0-6`)；读转 ST、写保形合并，`Raw` 原样回传 |
| entries 容器形态 | 数组/对象**不能互换**；v0.6.0 起 /api/lore 按容器保形（GET 回传 container，PUT 按容器写回） |
| PNG 卡 | 保存必须一次重写 chara+ccv3 两个块（`WriteTexts`） |
| JSON 卡 | 保存同步根级 V1 镜像（`SyncLegacyMirror`） |
| 索引版本 | 改 `LibraryItem` 字段或识别规则 → `IndexVersion` +1（当前 4；v0.6.1 因回撤 5 类由 3→4） |
| 条目 Id | 路径哈希；改名/移动后必须迁移用户数据快照 |

## 故障排查

| 症状 | 原因 / 解法 |
|---|---|
| 构建"成功"但跑的是旧代码 | exe 锁 DLL。`taskkill -IM TavernVault.exe -F` 后重建 |
| 前端改动不生效 | 对比 `bin/.../wwwroot` 与源目录时间戳；CopyFrontendFiles Target 偶发漏拷 |
| `node --check` 通过但运行报错 | script 模式校验没抓到模块错误，改用 `.mjs` 校验 |
| 页面白屏/模块加载失败 | 浏览器控制台读 `window.__errs`（index.html 内置探针） |
| `--data` 目录没生效 | Git Bash 吞了反斜杠；用相对路径 `--data=.smoke/data` |
| 仓库/Release 目录体积莫名增长 | WebView2 浏览器缓存 `bin/.../TavernVault.exe.WebView2\`（窗口模式每次运行都会增长，曾积累到 69M）。可整体删除，重开自动重建；`--server` 模式不产生 |
| git push 连不上 github | 代理 7890 未启动；仓库已配 `http.proxy` |
| 页面能打开但请求全 401 | 外部浏览器没有令牌（预期）——UI 只能经 WebView2 外壳使用；脚本用 `server-connection.json` 里的 token |
| 保存返回 409「文件已被外部修改」 | 文件在外部被改动或另一窗口已保存。**v0.5.2 起前端会自动重扫索引**并提示重新打开该条目；连续两次保存第二枪报 409 属预期（第一次已改 mtime） |
| 启动提示「已在运行」 | 单实例 Mutex 防护：**同一数据目录**只允许一个实例（v0.5.1 起按数据目录隔离，窗口模式 + `--server` 冒烟可并存） |
| 备份目录不可用/磁盘满/被删除 | 保存会继续但响应带 `warnings`，界面弹错误色提示，日志落 `logs/tavernvault-*.log`；目录被删后下次备份自动重建（v0.5.1）——检查备份位置 |
| 启动后库全空但资源还在 | settings.json 损坏被重置（坏文件已保留为 `settings.json.corrupt-*`，日志与 `/api/meta.settingsWarning` 有告警）：重新登记库目录后重扫即可找回收藏/标签（索引有 `index.bak` 留档） |
| 库里出现 `xxx.png.tmp-xxxx` 之类残片 | 旧版残留（v0.5.1 起扫描已过滤 `.tmp` 前缀且写失败自动清理），手动删除即可 |
| 酒馆源文件改名被拒 (403) | 预期护栏；确认风险后请求体加 `force:true` |
| 酒馆文件没有编辑按钮 / PUT 返回 403 | v0.7.1 预期行为：酒馆不实时读外部修改、还会用内存旧数据回写覆盖，就地编辑已退役。点「导出副本到局外存储」→ 编辑副本 → 用酒馆自带导入写回 |
| 在应用里改了文件，酒馆里看不到 | 同上——外部修改酒馆不会实时/可靠读取；且酒馆界面操作可能把旧数据写回覆盖你的修改（资源管家重进也看不到的原因）。可靠路径只有导出副本+酒馆自带导入 |
| 改过的文件忘了是哪个 | 侧栏「修改历史」：应用内保存/还原/重命名/移动过的文件按最近写入倒序，点击直达详情（酒馆侧直接改动不在此列） |
| 外部/酒馆侧改动没出现在列表 | v0.7.2 起自动重扫（防抖 0.8s + 前端 5s 轮询，实测 ≤8s 可见）；若仍无更新确认 `settings.json` 的 `AutoWatch` 未被改回 false，或点「重新扫描」兜底 |
| 数据/备份/日志存在哪 | 库设置「存储位置」显示数据目录路径，可一键打开；备份默认在其 `backups\` 下，可自定义 |
| 旧索引缺新字段 | 版本门控没触发？确认 `IndexVersion` 已 +1 |
| 修改后收藏/标签丢失 | 检查重命名/移动路径是否调了 `GetUserData`→`SetUserData` 迁移 |
| 整窗一起滚 | 已修复（v0.4.3）：`html/body overflow:hidden` + `#content flex:1 min-height:0 overflow-y:auto`。新布局元素若破坏滚动隔离，检查中间是否缺 `min-height:0`（flex 子项默认 min-height:auto 会撑破） |
| 手风琴点了没反应 | 分区为空时带 `.disabled`（预期置灰不响应）；确认 `.acc-head` 点击事件绑定在 `initAccordion()`（initShell 内） |

## 版本号规范（fix-1 起）

- **格式**：csproj 四段 `主.次.修订.热修`；显示 `vX.Y.Z`（热修段为 0）或 `vX.Y.Z fix-N`（≥1）
- **进位**：主版本 X = 重大重构/不兼容；次版本 Y = 新功能迭代（一次迭代一个小版本）；修订 Z = bug 修复累计（进位时热修段归 0）；热修 F = 发布后紧急修复（不加新功能，连修递增 fix-2…）
- **显示链同步点**：`TavernVault.App.csproj` `<Version>` → `/api/meta`（读程序集 `ToString(4)`）→ 前端 `main.js updateVersion()` 转换显示。改版本只改 csproj 一处
- **commit 首行**：`vX.Y.Z(-fixN)：主题`

文档演进规则（防版本历史无限膨胀）：

- 小修复/热修**不新增版本条目**，直接附在对应版本行内容后（如 "v0.4.3 | 主题A；fix-1 修复弹窗滚动"）
- 版本表每版本主题列 ≤2 行；架构图时间线每版本 ≤3 个分项，同类主题合并
- 历史版本条目不罗列细节，细节看 `development-handoff.md` §9 对应行

## 扩展点

| 想加什么 | 改哪里 |
|---|---|
| 新 REST 端点 | `ApiServer.MapApi`（记得 Handle 包装 + 更新冒烟脚本） |
| 新编辑器视图 | `editor.js` + index.html 模板 |
| 新资源类型 | `ItemKind` 枚举 + `ItemKindText.All` + `TypeDetector` 识别逻辑 |
| 新设置项 | `AppSettings` + `main.js` 设置弹窗 + 对应 API |
| 新备份策略 | `BackupStore.RetentionFor` 钩子（按库根来源分流） |

## 安全红线

1. 用户真实资源库（局外根、两个酒馆目录）**只读验证**，写测试只进 `testdata/` 临时目录
2. 删除一律走回收站，不直接删文件
3. 移动目标必须在已登记库根内（`GuardUnderRoots`）
4. API 只绑定 127.0.0.1，**不要去掉会话令牌 / Host 校验中间件**（v0.5.0 安全边界，见 README「安全模型」）
