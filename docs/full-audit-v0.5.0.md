# TavernVault 全面审查报告（v0.5.0 · 全量）

> **修复状态（2026-09-02）**：
> **v0.5.1 已修复**——P0-1 XSS；P1-1（设置损坏防护）、P1-5（ReparsePoint）、P1-6（路径逃逸）、P1-9（缩略图随数据目录）、P1-4（manifest 原子写）、N1/N2/N3；另有备份/缩略图目录自愈、List 磁盘过滤、tmp 前缀过滤、ResolveDataDir 绝对路径化。验证：单测 49/49、同数据目录冒烟 3 轮 81/81。
> **v0.5.2 已修复**——P1-2（Load 保留幽灵记录 + LoadWarning）、P1-3（RelocateTo 两阶段）、P1-7/P1-8/P1-10（编辑器 Tab/互刷/Esc 重构）、N4（move 备份）、N5（409 自动重扫 + 抽屉重取）、P2-2（异常收编 + catch 补 OCE + 通用文案）、P2-4（请求体上限 21MB）、P2-6（NewWindow 协议白名单 + NavigationStarting 拦截）、P2-1（WebView2 UDF 搬家）、P2-13（AppLog 跨天清理）、前端杂项（refreshItems 竞态、401 文案、tv-view、#loading、removeRoot）、测试缺口 A2/A4/A5/A6/A9（TavernGuardTests 6 项 + 冒烟酒馆护栏/错误合同两段 + 删 Unit1）。验证：单测 54/54、同数据目录冒烟 2 轮 105/105。
> 剩余项以 §8 路线图为准（v0.6：集成测试收编 + CI、前端拆分、备份健康度、发布文档）。
> 审查日期：2026-09-01 · 对象：v0.5.0（commit c63c8aa） · 方法：4 个并行子审计（Core 数据完整性 / 前端 / App 宿主与 API 安全 / 测试与文档）逐文件精读 + 主审交叉复核全部 P0/P1 证据 + 动态验证（构建 0 警告、44/44 单测、干净冒烟 74/74、探针复现）。
> 本报告取代此前两份评审（`architecture-review.md`、`v0.5.0-verification-and-plan.md`）成为当前未修问题的权威清单。已知未修 N1~N6 见 §1，此处不展开。

---

## 0. 结论速览

**本轮 4 路深审共确认 1 条 P0、10 条 P1、约 20 条 P2，外加 12 项测试缺口与 14 处文档失真。**

最重要的三个结论：

1. **drive-by 防线被前端自己绕过了**：预设可视化编辑器一处未转义的 `role` 字段（P0-XSS），让"下载的第三方预设"可以在 WebView2 内执行脚本、读取 `window.__TV_TOKEN__`、调用全部 31 个 API——v0.5.0 辛苦建立的令牌体系对这一条路径无效。修复是一行 `escapeHtml`。
2. **"静默吞异常 + 用内存态覆盖磁盘态"是全项目的系统性模式**：settings.json 读取失败 → 空根 → 启动自愈把 index.json 重写为空（收藏/标签永久丢）；备份目录瞬时不可见 → Load 过滤 + 下次写盘 → 全部备份记录成孤儿；RelocateTo 中断 → manifest 与文件错位、半写备份日后可覆盖回用户文件。三条都发生在"安全网"本身。
3. **圣域边界有两条真实破口**：扫描器跟随 junction（库外文件被索引后可被应用改删、环状 junction 死循环）；内嵌世界书导出把卡片 `name` 字段未经清洗拼进文件路径（可向库根外写文件）。

同时必须说明：**中间件本身（Host 白名单/令牌/恒定时间比对/大小写/编码）经逐项核对未发现绕过缝隙**；SHFileOperation 封送、GuardUnderRoots 前缀比较、ComputeId、增量复用等此前存疑项均核实为正确。代码库的基础素质仍在同类上游，问题集中在"装配处失忆"与"异常路径的乐观假设"。

---

## 1. 既有未修项状态（N1~N6，截至本报告未动）

N1 Restore 满上限自逐出、N2 冒烟清理路径双层嵌套、N3 Mutex 所有权与粒度、N4 move 无备份、N5 409 前端无恢复路径、N6 handoff §9.2 状态段漂移——**全部维持未修**。本报告把它们并入 §5 修复路线图。

---

## 2. P0（1 条）

### P0-1 预设可视化编辑器 XSS：第三方预设文件可在 WebView2 内执行任意脚本并窃取 API 令牌

**位置**：`src/TavernVault.App/wwwroot/js/editor.js:662、669、709`

```js
const role = isMarker ? '系统' : (p?.role ? (ROLE_LABELS[p.role] || p.role) : '—');
...
<span class="po-name">${escapeHtml(name)}</span>
<span class="po-role">${role}</span>        ← 未转义（相邻的 name 却转义了）
```

`p` 来自 `api.text(item.id)` 读出的**磁盘上的第三方预设 JSON**。当 `role` 不是 `system/user/assistant` 时回退为原值裸拼进 innerHTML。形如 `"role": "<img src=x onerror=…>"` 的预设，用户点"编辑"的瞬间即执行（`renderVisual()` 自动渲染，无需交互）。

**影响链**：WebView2 内同源脚本可读 `window.__TV_TOKEN__` → 调用全部 31 个 API（删/改/移动任意资源文件，注册任意库根）。**令牌防线对此无效——攻击已经在令牌持有方内部执行**。这正是 v0.5.0 安全模型要防的"下载的不可信内容"，只是入口从 HTTP 换成了文件内容。

**修复**：两处 `${escapeHtml(role)}`（editor.js:669、709）；顺手加固同模式的 `${title}`（editor.js:30）、`${f.source}`（main.js:206-211）；冒烟补一条 role 含 HTML 的预设夹具。**一行级改动，应最先做。**

---

## 3. P1（10 条）

按主题分组；每条均已经主审复核代码证据。

### 3.1 "静默吞异常 + 内存态覆盖磁盘态"集群（Core）

**P1-1 settings.json 读取失败 → 启动自愈把 index.json 重写为空，收藏/标签永久丢失**
`SettingsStore.cs:29-38`（`catch (JsonException or IOException) {}` 返回空 AppSettings）+ `Vault.cs:46-48`：roots 为空时 `RootContaining(...)?.Source != i.RootSource` 对任何条目恒为 true → 构造函数必然触发 Rescan → 零根扫描 → `SaveIndex(空)`。settings.json 只要损坏或被瞬时锁定（杀软/同步盘很常见），**收藏与用户标签（只存在于 index.json）即被静默清空**。
修复：LoadSettings 区分"不存在"（默认）与"存在但读不了"（fail-fast 或备份坏文件后中止）；自愈 Rescan 加条件（`LibraryRoots.Count > 0 || Items.Count == 0`）；index.json 落盘前轮转 `index.bak`。

**P1-2 BackupStore.Load 过滤 + 任意写触发 Save：备份目录瞬时不可见一次，全部备份记录被清成孤儿**
`BackupStore.cs:207-212`（Load 时按 `File.Exists` 过滤）+ :214-221（任何一次写操作把过滤后的内存列表整体写回 manifest）。移动盘/网络盘未挂载、目录被临时改名，重启后备份列表清零且不可恢复。
修复：缺席记录保留在旁列表，Save 合并写出；缺席比例异常时拒绝 Save 并告警。

**P1-3 BackupStore.RelocateTo 中断 → manifest 与物理文件永久错位；半写文件日后可覆盖回用户原文件**
`BackupStore.cs:60-82`：跨卷迁移 = Copy+Delete，目标盘满/USB 拔出时留下半写目标文件，catch(IOException) 却照常把记录搬入新 manifest 并删旧 manifest。此后"还原"可能把**截断的字节流覆盖回原角色卡**（:152 的 File.Copy）。
修复：两阶段迁移（Copy 到 `*.reloc-tmp` → 长度校验 → Move）；失败记录不迁移；全量成功才删旧 manifest。

**P1-4 BackupStore manifest.json 非原子直写**（App 审计 P2-7，升格并入本组）
`BackupStore.cs:214-221` 直写，断电窗口截断 → Load 捕 JsonException → `_manifest=[]` → 全部备份从 UI 与还原接口消失。与 `SaveIndex`/`SaveSettings` 的原子写不一致。
修复：tmp+Move 同款；Load 失败时保留坏文件并告警。

### 3.2 圣域边界破口（Core/App）

**P1-5 扫描器跟随 junction/符号链接：环状 junction 死循环；指向库外的 junction 把外部文件纳入"可改删"范围**
`LibraryScanner.cs:33`：`AttributesToSkip = Hidden | System`，**缺 `FileAttributes.ReparsePoint`**——.NET 官方语义是缺此项就会下钻 reparse 目录。库根里一个 `mklink /J loop .` 可使 Rescan 永不返回；一个指向 `Documents` 的 junction 让外部文件以合法条目身份进入 delete/编辑/还原流程，绕过 GuardUnderRoots 的拒绝路径。
修复（一行）：`AttributesToSkip` 加 `ReparsePoint`；如需支持库内链接，解析 LinkTarget 后再判 RootContaining。

**P1-6 内嵌世界书导出：卡片 `name` 未经清洗直入文件路径，可向库根外写任意 JSON**
`ApiServer.cs:315-321`：`Path.Combine(dir, item.DisplayName + ".json")`，而 `LibraryScanner.cs:161` 的 `item.Title = AsString(data["name"])` 是全项目唯一**没有过 `Clean()`** 的内容字段（对比 :164/209 全部有 Clean）。`data.name` 为 `C:\Users\Public\x` 或 `..\..\x` 时，`Path.Combine`/`GetSaveAsPath` 均不拦截，`File.WriteAllTextAsync` 按原样解析——打破"全部文件操作限制在库目录内"的核心不变量（FileOperations.cs 类注释自述）。
修复：Title 加 `Clean(…, 200)`；`GetSaveAsPath` 内对 stem 做文件名字符清洗 + 对最终路径断言 `GuardUnderRoots`；回归用例：name 含绝对路径/`..\` 的卡各一条。

### 3.3 前端编辑器的静默数据丢失（集中在 editor.js 角色卡编辑器）

**P1-7 Tab 切换监听器累积 + 模块级 dirty 清除 → 两条静默丢失路径**
editor.js:226（每次重建对同一 `#editor-tabs` addEventListener）+ :7（模块级 dirty）+ :231-234。表单↔JSON 来回切换后：旧监听器对已脱离 DOM 的表单 `applyFormToCard()` 并 `clearDirty()` → 丢失"放弃修改"保护；此时 Ctrl+S 用**不含表单编辑的旧 JSON 整卡写盘**（:265-268）。
修复：Tab 改事件委托绑一次（或 `AbortController` 重建时移除）；dirty/saveFn 绑定到构建实例而非模块级。

**P1-8 保存成功后另一视图不刷新 → 从陈旧视图保存会整体回滚刚完成的保存**
editor.js:283-291 保存成功只更新内存 `card`，raw.area 与表单 DOM 都不动；此后切视图 `if (dirty)` 为 false 不同步 → 用户在旧 JSON 上保存，把上一次保存覆盖掉。预设编辑器（:879-883）已做对，角色卡编辑器漏做。修复：保存成功后与预设编辑器对齐互刷两视图。

### 3.4 宿主装配

**P1-9 缩略图目录硬编码 `%APPDATA%`，`--data=` 不生效**（主审此前发现的线索，子审计确认）
`ThumbnailService.cs:17-23` 构造函数自取 %APPDATA%，`ApiServer.cs:44` 明明手握 `vault.DataDir` 却不传。后果：① 冒烟污染开发者真实数据目录且从不清理；② 缓存键 = 路径哈希 → **测试与生产缩略图跨数据目录串台**，冒烟非密封；③ 顺带：失效键比较"缓存文件 mtime >= 条目 mtime"，还原旧备份后 mtime 回退 → 陈旧缩略图被判新鲜。
修复：`new ThumbnailService(vault.DataDir)`；失效键改为旁车记录源文件 mtime+size。

**P1-10 Esc 级联紊乱：编辑器 dirty 确认框每按一次 Esc 新弹一个，Esc 永远关不掉编辑器**（前端审计定 P1，主审调整为偏 UX 的 P1 下限）
editor.js:45-58 的 capture 监听器先于 confirmDialog 的 capture 监听器注册，`stopPropagation` 拦不住同节点先注册者 → closeEditor 重入弹新框；且确认框悬空期间 Ctrl+S 被 onCloseCleanup 接管仍可写盘。修复：onCloseCleanup 检测已有 `.modal-mask` 则 return，或全局 Esc 统一为"按栈顶组件分发"。

---

## 4. P2 清单（20 条，按主题归并）

**Core / 数据**
1. `WriteTexts` 遇畸形 chunk 静默截断写回，产物丢 IEND 及其后全部块；`File.Replace` 失败路径残留 `.tmp-*`（PngChunkIO.cs:86-95,116-123）。
2. 残留 tmp 命名 `.tmp-xxxx` 绕过扫描器 `.tmp` 过滤，半成品 PNG 会被索引为正式条目（PngChunkIO.cs:86 vs LibraryScanner.cs:39）。修：过滤改 `StartsWith(".tmp")` + tmp try/finally 清理。
3. PNG 保存分支重建 payload 根对象，根级未知字段全部丢弃——与 JSON 分支的 SyncLegacyMirror 策略不对称，违反"无损编辑"承诺（CharacterCardFile.cs:55-79）。
4. `CharacterBook` 对类型不匹配的 JsonValue 抛异常 → 该卡内嵌书编辑器 500 打不开；MergeIntoSpec 给原本缺失的键注入默认值（CharacterBook.cs:110-160）。社区卡 `"enabled":"true"`（字符串）即触发。
5. 锁纪律：`RootContaining`/`BackupBeforeWrite` 在 Vault 锁外遍历 roots，与 AddRoot 并发抛 InvalidOperationException；`MaxPerFile` 共享可变，并发写不同来源时轮转按错误份数执行（Vault.cs:74-84,43-44）。
6. Rename 无条件追加原扩展名，输入"卡.png"得到"卡.png.png"，无校验无提示（FileOperations.cs:32）。

**App / API**
7. 异常处理双轨制：9 个端点未经 Handle/HandleAsync 包裹（IO 异常 → 无日志 500 诊断黑洞）；catch 列表缺 `OperationCanceledException`/`SecurityException`；三处裸 `catch Exception → 500` 把含绝对路径的原始消息回给客户端（ApiServer.cs:716-739 等）。
8. ThumbnailService：残缺 tmp 被当成功结果写入缓存；STA `thread.Join()` 无超时 + gate 无取消，两张慢图占满并发（ThumbnailService.cs:40-44,56-85）。
9. 写路径无大小上限：20MB 只挡读（且用索引值，外部换文件后失真），Kestrel 默认 30MB 是唯一约束；超 20MB 文件"写入即自锁"再也无法 GET（ApiServer.cs:462 vs 466-488）。
10. Rescan 全程持锁做全库 IO，`/api/roots`、`/api/rescan`、tavern/connect 在请求线程内触发 → 库大时整个 API 冻结；`/api/meta` 的 SerializeRoots 每根一次全量 Query，O(roots×n log n)（Vault.cs:89-102）。
11. NewWindowRequested 对任意 URI `Process.Start`（`file:///x.exe`、shell: 均放行）；令牌按 WebView2 语义注入"一切文档"且无 NavigationStarting 拦截——将来引入同窗口外部导航即令牌外泄（MainWindow.xaml.cs:26-36）。前置条件是先有渲染层漏洞（如 P0-1），属纵深加固。
12. WebView2 UDF 默认 exe 旁：Program Files 场景启动失败且 catch 只捕 `WebView2RuntimeNotFoundException` → 空白窗口；UDF 也该搬进数据目录（MainWindow.xaml.cs:20,38-43）。
13. AppLog：跨进程写同名日志丢行（静默）；磁盘满时唯一诊断通道恰好失效；prune 仅启动时一次（AppLog.cs）。修 N3（Mutex 按数据目录）时需一并确认。
14. App.xaml.cs：`DispatcherUnhandledException` 无条件 `Handled=true`；`OnExit` async void 续体可能不执行（Kestrel 非优雅停机）；`--server` 失败仍弹 MessageBox 挂起无头脚本。
15. csproj：双套前端拷贝机制无分工注释；无签名/Publisher（个人项目可接受，建议记录决策）。

**前端（编辑器外的独立小项）**
16. `saveFn` 模块级残留：打开 B 加载失败时，Ctrl+S 会把上次会话 A 的内容 PUT 回 A（editor.js:113-119,77-80）。
17. `refreshItems` 无请求序号，并发响应乱序覆盖 `state.items`，列表与筛选条件脱节且不自愈（app.js:173-191）。
18. 连点保存无 in-flight 防护 → 第二次稳定 409，叠加 N5 观感上锁死（editor.js:42）。
19. 其余小项：全局 Esc 兜底取**最底层** mask 且 remove 不 resolve 挂起 promise（app.js:672）；raw 解析失败切 Tab 静默丢弃编辑（editor.js:241-245）；**另存为成功后继续 Ctrl+S 写的是原文件**（editor.js:298-316，同型 ×4）；世界书搜索框输入即标 dirty（editor.js:341,457）；401 报成"无法连接本地服务"（main.js:295）；`tv-view` 无白名单校验（对照 `tv-library` 有）；boot 失败 `#loading` 永久停留；removeRoot 失败静默；编辑器头部超长文件名把保存按钮挤出视口（css:335-343）。

---

## 5. 测试缺口（来自测试专项审计，按风险排序）

结构性结论：**现有覆盖全部落在"Core 乐观路径 + v0.5.0 新特性回归"，v0.4.0 的酒馆护栏支柱与全部错误分支处于零断言状态**——N1 恰好存活于这种缝隙。

- **T1（P0 级）N1 的回归用例不存在**：现有测试最多 2 份备份，与"满上限还原"永不相交。修 N1 时必须同步补：单测（MaxPerFile=3、写 4 份、还原最早）+ 冒烟（API 显式堆满 5 份再还原——干净夹具永远到不了上限）。
- **T2（P0 级）酒馆护栏零自动化**：grep 证实 tests/ 内 0 处引用 TavernDetector；"酒馆源默认 403、强制备份、TT 10 份"三条安全承诺无任何断言，重构删掉 `isTavern` 判断全测试照绿。补法全部不需要真实酒馆：单测（TT 来源 + AutoBackup=false 仍备份；RetentionFor TT=10；TavernDetector 环境变量优先级）；冒烟（注册假 tavernST 根 → rename 无 force 403 / 带 force ok+备份+1；connect 非法来源 400）。
- **T3（P1）`POST /api/settings/backup` 全分支零覆盖**（clamp、相对路径 400、空串恢复默认、非法目录）——该端点会实际迁移备份文件，误输入破坏半径大。
- **T4（P1）409 只测了 text PUT**；cards/book/lore 三个 PUT 的 CheckNotModified 零覆盖（恰是前端全部保存走的路径）。
- **T5（P1）错误合同批量缺失**：未知 id/bid 404、错 kind 的 saveas、非文本扩展名 400、roots 空路径、rename 双扩展名行为、thumb 404、image Range、pick-folder headless 400、DELETE roots、categories、reveal。
- **T6（P2）sort 四种排序全链路零覆盖**（冒烟从未传过 sort）。
- **T7（P2）smoke `call()` 不校验响应形状**：错误响应 `{"error":...}` 是 dict，`len(items)==1` 断言可被恰好长度 1 的错误对象骗过 PASS，随后 `items[0]` KeyError 崩溃——失败被伪装成断言失败而非清晰报错。
- **T8（P2）`UnitTest1.cs` 空壳模板残留**（44 报告 vs 43 有效），handoff 还把它当正式文件列出。
- **T9（P2）SettingsStore 版本门控/损坏容错、TypeDetector 兜底、点目录过滤无直测。**
- **T10（P2）冒烟可复现性守卫缺失**：不检测数据目录是否新鲜（配合 N2 二连跑即红）；三份文档均未写"需全新数据目录"。

## 6. 文档失真（14 处，要点）

- **端点数 31 vs 实际 35**（architecture-visualization.md:20；v0.4.3 起就是 35）。
- **server-url.txt 残留两处**（visualization:73,244——代码早已写 server-connection.json）。
- **测试数硬编码三处违反自立的收敛规则**（visualization:234-235 "36/49"、handoff:45 "41"——实际 44/74）。
- **§3 时序图仍画"备份→写盘→重扫"**：代码已是 UpsertItem 增量更新，且图中无 409/warnings（visualization:79,103）。
- **§8 决策树 move 画了备份+Rescan**（N4 本体 + 增量更新未回灌）；§12 象限图"四重防护含强制备份"对 move 过度陈述。
- **§4 扫描流程图两处技术性错误**：版本门控画在扫描尾部（实际在加载时）；"LastScanAt 落盘"（实际只在内存）。
- **quick-reference 409 指引会把用户锁死**（"重新打开会重扫"——实际 openDrawer 不重取不重扫，N5 文档侧）。
- **handoff:311 "自清理上轮残留"与 N2 现实矛盾**；三份文档无一写"冒烟需全新数据目录"。
- **README 与 quick-reference 冒烟命令不一致**（有无 PYTHONIOENCODING 前缀）。
- **st-sync 两处历史陈述过时**（探测校验规则、"写前原子替换"——JSON 写路径是直写）。
- **单实例提示语与 Mutex 粒度不符**（"同一数据目录"vs 实际整机，且阻断"窗口开着跑冒烟"的文档化流程）。
- **"写操作只作用于 testdata/"不完全为真**（thumbs 写进真实 %APPDATA%，P1-9 的文档侧）。
- **版本号规范毛边**：quick-reference 规定 `vX.Y.Z-fixN` 连字符，实际提交是 `vX.Y.Z fix-1` 空格。
- **杂项**：`docs/v0.5.0-verification-and-plan.md` 与 testdata 目录 untracked 未 gitignore；"EnsureDefaultRoot 探测等约定路径"表述不准（只探测酒馆PR 一个）。

## 7. 核实为正确的项（负向结论，供对照免重复怀疑）

SHFileOperation 双 null 封送（惯用法成立）；GuardUnderRoots 前缀比较（8.3 短名只会误拒 fail-closed）；RevealInExplorer 无注入面；ComputeId SHA256-64bit 碰撞可忽略；**令牌中间件无绕过缝隙**（StartsWithSegments 忽略大小写+段边界、`/%61pi` 解码后命中、`//api` 路由不匹配、WebSocket 同样过中间件、Host 校验 fail-closed）；`GetValue<T>` 异常被统一包装；FolderPicker；CopyFrontendFiles 与 .mjs 校验法。CharacterBookTests（10 项）与 Guard 测试是全项目质量最高的测试。

---

## 8. 修复路线图

### v0.5.1（P0/P1 中的一行级修复 + 既有 N 项，预计 1-2 天）

| # | 事项 | 量级 |
|---|---|---|
| 1 | **P0-1 XSS**：两处 escapeHtml + 预设 role 夹具冒烟 | 一行×2 |
| 2 | **N1** Restore 自逐出（先快照源再安全备份）+ 原子落盘 + T1 回归用例 | 小 |
| 3 | **P1-5** AttributesToSkip += ReparsePoint | 一行 |
| 4 | **P1-6** Title 加 Clean(200) + GetSaveAsPath 文件名清洗 + GuardUnderRoots 断言 | 小 |
| 5 | **P1-1** 设置读取失败 fail-fast + 自愈条件收紧 + index.bak 轮转 | 小 |
| 6 | **N2** 冒烟清理路径修正 + 数据目录新鲜度守卫 + `.gitignore` 补 testdata*/（并提交未跟踪的两份评审文档） | 小 |
| 7 | **N3** Mutex 所有权 + 名掺 DataDir 哈希（与提示语对齐） | 小 |
| 8 | **P1-9** ThumbnailService 注入 DataDir | 一行 |

验收：干净构建 0 警告；单测全绿；**同一数据目录连续两轮冒烟全绿**；新增 P0-1/P1-6/N1/T2 各自的回归用例全绿。

### v0.5.2（可靠性集群 + 编辑器重构 + 补测）

9. P1-2/P1-3/P1-4 备份元数据可靠性三连（Load 缺席保留、RelocateTo 两阶段、manifest 原子写）——各配故障注入式回归。
10. P1-7/P1-8/P1-10 编辑器一次性重构（Tab 事件委托 + 保存后互刷 + Esc 栈顶分发），顺带清 P2-16/18/19 的编辑器内项。
11. T2 酒馆护栏测试 + T3~T5 错误合同批补；T8 删 UnitTest1。
12. N5 409 恢复路径（drawer 重取 + 自动 rescan）；N4 move 补备份。

### v0.6（工程化）

13. API 集成测试收编 `dotnet test`（TestServer）+ GitHub Actions CI——P2-7 的异常双轨制、P2-9/10 的上限与性能在此时一并拉平。
14. P2-11/12 WebView2 纵深（NavigationStarting 拦截、UDF 搬家、NewWindow 仅 http/https）。
15. 架构图集全面回灌（端点数/时序图/决策树/版本门控位置），并把"数字一致性 + Mermaid 与代码 diff 核对"写进发版清单。
16. 发布/分发文档与备份故障恢复手册（上轮遗留，仍未做）。

---

## 9. 总评

v0.5.0 的五项修复经受住了全量复审，令牌中间件经专项攻击面核对无绕过，Core 的关键路径（PNG 透传、护栏、回收站）逐行核对站得住——**骨架依然是健康的，无需重构**。这轮全量审查把问题画像从"有没有大 bug"推进到了"哪一类 bug 会反复出现"：**攻击面不在网络层，而在"不可信文件内容流入文件系统路径与 DOM"这两个装配点**（P0-1、P1-6、P1-5 全是同一模式）；**可靠性短板不在正常路径，而在异常路径的乐观假设**（§3.1 集群全部是"出错时假装没出事、然后用内存态覆盖磁盘态"）；**测试的短板不在质量而在分布**——全部防线压在 Core 乐观路径上，安全承诺（酒馆护栏）与错误合同零断言。修复清单里一半以上是一行到几行的改动，且这个项目的测试纪律足以让每条修复都配上回归钉——按 §8 的顺序执行即可。
