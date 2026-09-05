using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;
using TavernVault.App.Services;
using TavernVault.Core;
using TavernVault.Core.Cards;
using TavernVault.Core.Collect;
using TavernVault.Core.Detection;
using TavernVault.Core.FileOps;
using TavernVault.Core.Models;
using TavernVault.Core.Scanning;
using TavernVault.Core.Storage;

namespace TavernVault.App.Hosting;

/// <summary>Build 的返回值：App 供启动，Token 供 WebView2 注入 / 连接文件，DataDir 供落盘位置。</summary>
public sealed record ApiServerHandle(WebApplication App, string Token, string DataDir);

/// <summary>Kestrel 本地服务：静态前端 + REST API。只绑定 127.0.0.1。</summary>
public static class ApiServer
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".json", ".js", ".mjs", ".ts", ".py", ".qs", ".ps1", ".bat", ".yaml", ".yml", ".md", ".txt", ".css", ".html", ".log" };

    public static ApiServerHandle Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        var port = Array.Find(args, a => a.StartsWith("--port="));
        builder.WebHost.UseUrls(port is null ? "http://127.0.0.1:0" : $"http://127.0.0.1:{port[7..]}");
        // v0.5.2 修复 P2-4：写路径显式上限——略高于 20MB 文本读取上限，从 Kestrel 默认 30MB 变为显式契约，
        // 避免超限文件"写入即自锁"后连 GET 都无法通过
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 21_000_000);

        var vault = new Vault(new SettingsStore(ResolveDataDir(args)));
        AppLog.Init(vault.DataDir);
        AppLog.Info($"启动 v{typeof(ApiServer).Assembly.GetName().Version?.ToString(4)}（数据目录 {vault.DataDir}）");
        // v0.5.2 修复 P1-2：设置告警与备份目录缺席告警合并为一条，经日志与 /api/meta.settingsWarning 外显
        if (CombinedWarning(vault.SettingsWarning, vault.Backups.LoadWarning) is { } warn) AppLog.Warn(warn);
        EnsureDefaultRoot(vault);
        var thumbs = new ThumbnailService(vault.DataDir);
        var headless = args.Contains("--server");
        var token = Array.Find(args, a => a.StartsWith("--token=")) is { } t
            ? ValidateToken(t[8..])
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var app = builder.Build();
        // 安全边界（管道最外层）：Host 白名单防 DNS rebinding；/api/* 须携带会话令牌
        // （X-TV-Token header 或 ?token= query——img src 无法带自定义 header，两通道保密性等价）。
        // 浏览器 drive-by 的跨域 no-cors 请求无法得知随机令牌，防住本机网页对 API 的增删改。
        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host.Trim('[', ']');
            if (host != "127.0.0.1" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "::1")
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "拒绝的 Host 头" });
                return;
            }

            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var provided = ctx.Request.Headers["X-TV-Token"].FirstOrDefault()
                    ?? ctx.Request.Query["token"].FirstOrDefault();
                if (provided is null || !TokenMatches(provided, token))
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "未授权：缺少或无效的访问令牌" });
                    return;
                }
            }
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // 本地单用户服务：始终重校验，避免更新前端文件后被缓存卡住
            OnPrepareResponse = ctx =>
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate",
        });

        var watcher = new VaultWatcher(vault);
        watcher.Start(); // 库根文件监视（v0.7.2）：外部改动防抖自动重扫；App 退出时随容器释放
        app.Lifetime.ApplicationStopping.Register(watcher.Dispose);
        MapApi(app, vault, thumbs, headless, watcher);
        return new ApiServerHandle(app, token, vault.DataDir);
    }

    /// <summary>
    /// 解析数据目录（--data= 优先，否则 %APPDATA%\TavernVault）。
    /// 结果固定为绝对路径（启动时冻结，不随进程工作目录漂移）。
    /// App 启动期的单实例 Mutex 名也要用它，必须与 SettingsStore 的默认值保持同一来源。
    /// </summary>
    public static string ResolveDataDir(string[] args) =>
        Array.Find(args, a => a.StartsWith("--data=")) is { } d && d[7..].Trim().Length > 0
            ? Path.GetFullPath(d[7..])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TavernVault");

    private static string ValidateToken(string token)
    {
        if (token.Length < 16 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("--token 需要 ≥16 个字符且不含空白");
        return token;
    }

    private static bool TokenMatches(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>首次运行时把常见的酒馆资源目录作为默认库（存在才加）。不含机器特定路径。</summary>
    private static void EnsureDefaultRoot(Vault vault)
    {
        if (vault.Settings.LibraryRoots.Count > 0) return;
        var guess = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "酒馆PR");
        if (Directory.Exists(guess))
            vault.AddRoot(guess);
        // 不存在则保持空库，由前端空态引导用户在「库设置」中添加
    }

    private static void MapApi(WebApplication app, Vault vault, ThumbnailService thumbs, bool headless,
        VaultWatcher watcher)
    {
        // ---------- 元信息 / 扫描 ----------
        app.MapGet("/api/meta", (HttpContext ctx) => Handle(ctx, () =>
        {
            var (tags, total) = vault.AllUserTags();
            var libraries = vault.BuildLibraries();
            var kinds = ItemKindText.All
                .Select(a => new
                {
                    kind = ItemKindText.KeyOf(a.Kind),
                    label = a.Label,
                    count = libraries.Sum(l => l.Kinds.First(k => k.Kind == a.Key).Count),
                })
                .ToList();
            return Json(new
            {
                total,
                kinds,
                userTags = tags,
                roots = SerializeRoots(vault),
                libraries = libraries.Select(l => new
                {
                    key = l.Key,
                    label = l.Label,
                    total = l.Total,
                    rootCount = l.RootCount,
                    favorites = l.Favorites,
                    kinds = l.Kinds.Select(k => new { kind = k.Kind, label = k.Label, count = k.Count }),
                    dirs = l.Dirs.Select(d => new { root = d.Root, dir = d.Dir, count = d.Count }),
                    tags = l.Tags.Select(t => new { tag = t.Tag, count = t.Count }),
                }).ToList(),
                lastScanAt = vault.LastScanAt,
                settingsWarning = CombinedWarning(vault.SettingsWarning, vault.Backups.LoadWarning),
                dataDir = vault.DataDir,
                version = typeof(ApiServer).Assembly.GetName().Version?.ToString(4),
            });
        }));

        app.MapPost("/api/rescan", (HttpContext ctx) => Handle(ctx, () =>
        {
            int count = vault.Rescan();
            return Json(new { count });
        }));

        // ---------- 条目查询 ----------
        app.MapGet("/api/items", (HttpContext ctx, string? kind, string? q, string? tag, bool? fav, string? sort, string? dir, string? root, string? source) =>
            Handle(ctx, () =>
        {
            LibrarySource? src = null;
            if (!string.IsNullOrEmpty(source))
            {
                // 严格契约：非法来源返回 400，不静默当作 Normal
                if (source is not ("normal" or "tavernST" or "tavernTT"))
                    return Err("无效的库来源", 400);
                src = ParseSource(source);
            }
            var p = new QueryParams
            {
                Kind = kind is { Length: > 0 } && Enum.TryParse<ItemKind>(kind, true, out var k) ? k : null,
                Search = q,
                UserTag = tag,
                Favorite = fav,
                Sort = sort ?? "name",
                Dir = dir,
                RootPath = root,
                Source = src,
            };
            return Json(vault.Query(p));
        }));

        app.MapGet("/api/items/{id}", (HttpContext ctx, string id) => Handle(ctx, () =>
            vault.Find(id) is { } item ? Json(item) : Err("条目不存在", 404)));

        // ---------- 缩略图 / 原图 ----------
        app.MapGet("/api/thumb/{id}", async (HttpContext ctx, string id) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || !item.HasEmbeddedCard) return Results.NotFound();
            var path = await thumbs.GetAsync(item);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        }));

        app.MapGet("/api/image/{id}", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null || !item.HasEmbeddedCard) return Results.NotFound();
            return Results.File(item.FullPath, "image/png", enableRangeProcessing: true);
        }));

        // ---------- 角色卡编辑 ----------
        app.MapGet("/api/cards/{id}", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            try
            {
                var card = CharacterCardFile.Load(item.FullPath);
                if (card is null) return Err("无法解析角色卡数据", 400);
                return Json(new { fileName = item.FileName, card });
            }
            catch (Exception ex)
            {
                // v0.5.2 修复 P2-2：原始消息可能含绝对路径，只回通用文案；完整异常落日志
                AppLog.Error($"读取角色卡失败：{item.FullPath}", ex);
                return Err("读取失败，文件可能已损坏", 500);
            }
        }));

        // body: { fields: {...}, alternateGreetings: [...], tags: [...], expectedModified? } —— 服务端合并保存，避免整卡回传
        app.MapPut("/api/cards/{id}", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            if (TavernEditGuard(item) is { } g1) return g1;

            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body is null) return Err("请求体格式错误", 400);
            if (CheckNotModified(item, body) is { } conflict) return conflict;

            var card = CharacterCardFile.Load(item.FullPath) as JsonObject;
            if (card is null) return Err("无法解析原角色卡", 400);

            // 整卡替换（原始 JSON 模式）：必须仍是合法卡片结构
            if (body["card"] is JsonObject newCard)
            {
                if (newCard["data"] is null && newCard["name"] is null)
                    return Err("卡片结构无效：缺少 data/name", 400);
                card = newCard;
            }
            var data = CharacterCardFile.GetDataNode(card);

            if (body["fields"] is JsonObject fields)
            {
                foreach (var (key, value) in fields)
                {
                    var v = value is JsonValue val && val.TryGetValue<string>(out var s) ? s : null;
                    if (string.IsNullOrWhiteSpace(v)) data.Remove(key);
                    else data[key] = v;
                }
            }
            if (body["alternateGreetings"] is JsonArray greetings)
                data["alternate_greetings"] = (JsonArray)greetings.DeepClone();
            if (body["tags"] is JsonArray tags)
                data["tags"] = (JsonArray)tags.DeepClone();

            var warnings = new List<string>();
            AddWarnings(warnings, vault.BackupBeforeWrite(item.FullPath));
            CharacterCardFile.Save(item.FullPath, card);
            vault.UpsertItem(item.FullPath);
            return Json(new { ok = true, id = LibraryScanner.ComputeId(item.FullPath), warnings, modifiedAt = File.GetLastWriteTime(item.FullPath) });
        }));

        // ---------- 另存为（自动命名副本） ----------
        // 卡片：当前编辑内容写入新文件（PNG 先复制原图再重新内嵌，图像保留）
        app.MapPost("/api/cards/{id}/saveas", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var cardNode = body?["card"] as JsonObject;
            if (cardNode is null) return Err("请求体格式错误", 400);

            var newPath = FileOperations.GetSaveAsPath(item.FullPath);
            if (item.HasEmbeddedCard && item.FullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                File.Copy(item.FullPath, newPath); // 复制原图，Save 内只重嵌 chara/ccv3 块，图像数据原样保留
            CharacterCardFile.Save(newPath, cardNode);
            vault.UpsertItem(newPath);
            var nid = LibraryScanner.ComputeId(newPath);
            return Json(new { ok = true, id = nid, fileName = Path.GetFileName(newPath) });
        }));

        // 世界书：编辑后的条目写入新文件（其它顶层键保留）。
        // v0.6.0：容器保形——源文件 entries 为数组时按数组容器写回（同 PUT 的保形合并）。
        app.MapPost("/api/lore/{id}/saveas", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Lorebook) return Err("不是世界书", 400);
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["entries"] is not JsonArray list) return Err("请求体格式错误", 400);

            var root = JsonNode.Parse(File.ReadAllText(item.FullPath)) as JsonObject
                ?? throw new InvalidOperationException("原文件不是 JSON 对象");
            if (root["entries"] is JsonArray)
            {
                root["entries"] = new JsonArray();
                var entries = new List<CharacterBook.BookEntry>();
                foreach (var node in list)
                {
                    if (node is not JsonObject e) continue;
                    entries.Add(new CharacterBook.BookEntry
                    {
                        MapKey = e["key"]?.GetValue<string>() ?? entries.Count.ToString(),
                        St = e["data"] as JsonObject ?? new JsonObject(),
                        Raw = (e["raw"] as JsonObject)?.DeepClone().AsObject(),
                    });
                }
                CharacterBook.WriteEntries(root, entries);
            }
            else
            {
                var entries = new JsonObject();
                foreach (var node in list)
                {
                    if (node is not JsonObject e) continue;
                    entries[e["key"]?.GetValue<string>() ?? entries.Count.ToString()] = e["data"]?.DeepClone() ?? new JsonObject();
                }
                root["entries"] = entries;
            }

            var newPath = FileOperations.GetSaveAsPath(item.FullPath);
            await File.WriteAllTextAsync(newPath, root.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
            vault.UpsertItem(newPath);
            return Json(new { ok = true, id = LibraryScanner.ComputeId(newPath), fileName = Path.GetFileName(newPath) });
        }));

        // 内嵌世界书：导出为独立世界书（ST dict 格式）
        app.MapPost("/api/cards/{id}/book/saveas", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["entries"] is not JsonArray list) return Err("请求体格式错误", 400);

            var entries = new JsonObject();
            foreach (var node in list)
            {
                if (node is not JsonObject e) continue;
                entries[entries.Count.ToString()] = e["data"]?.DeepClone() ?? new JsonObject();
            }
            var displayName = item.DisplayName;
            var dir = Path.GetDirectoryName(item.FullPath)!;
            // DisplayName 来自卡片内容（不可信），必须清洗为单段文件名，
            // 否则绝对路径/.. 会借 Path.Combine 逃逸库根（v0.5.1 修复）
            var safeName = FileOperations.SanitizeFileName(displayName);
            if (safeName.Length == 0) safeName = "未命名";
            var newPath = FileOperations.GetSaveAsPath(Path.Combine(dir, safeName + ".json"));
            var doc = new JsonObject { ["entries"] = entries };
            await File.WriteAllTextAsync(newPath, doc.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
            vault.UpsertItem(newPath);
            return Json(new { ok = true, id = LibraryScanner.ComputeId(newPath), fileName = Path.GetFileName(newPath) });
        }));

        // 文本/原始 JSON：内容写入新文件（.json 校验）
        app.MapPost("/api/text/{id}/saveas", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            var content = (await JsonNode.ParseAsync(req.Body))?["content"]?.GetValue<string>();
            if (content is null) return Err("请求体格式错误", 400);
            if (Path.GetExtension(item.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                try { _ = JsonNode.Parse(content); }
                catch (System.Text.Json.JsonException ex) { return Err($"JSON 校验失败：{ex.Message}", 400); }
            }
            var newPath = FileOperations.GetSaveAsPath(item.FullPath);
            await File.WriteAllTextAsync(newPath, content, new UTF8Encoding(false));
            vault.UpsertItem(newPath);
            return Json(new { ok = true, id = LibraryScanner.ComputeId(newPath), fileName = Path.GetFileName(newPath) });
        }));

        // ---------- 导出副本（v0.7.1，酒馆来源专用） ----------
        // 把酒馆目录里的文件字节级复制到第一个局外库根（"原名-副本 时间戳"命名），副本可直接编辑。
        // 这是编辑酒馆资源的可靠路径的第一步：导出 → 编辑副本 → 酒馆自带导入写回。
        app.MapPost("/api/items/{id}/export", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null) return Err("条目不存在", 404);
            if (item.RootSource == LibrarySource.Normal)
                return Err("该文件已在局外存储，可直接编辑，无需导出", 400);
            var targetRoot = vault.Settings.LibraryRoots
                .FirstOrDefault(r => r.Source == LibrarySource.Normal)?.Path;
            if (targetRoot is null)
                return Err("尚未登记局外存储库根：请先在「库设置」添加一个普通库目录作为导出目标", 400);
            var newPath = FileOperations.GetSaveAsPath(Path.Combine(targetRoot, item.FileName));
            File.Copy(item.FullPath, newPath);
            vault.UpsertItem(newPath);
            AppLog.Info($"导出副本：{item.FullPath} → {newPath}");
            return Json(new { ok = true, id = LibraryScanner.ComputeId(newPath), fileName = Path.GetFileName(newPath) });
        }));

        // ---------- 修改历史（v0.7.1） ----------
        // 程序内每次写入前都会自动备份 → 备份清单即"我在应用里改过哪些文件"的权威记录。
        // 按原文件聚合取最近写入时间倒序（含条目当前 id，前端可直达详情）；酒馆侧的外部改动不经此记录。
        app.MapGet("/api/history", (HttpContext ctx) => Handle(ctx, () =>
        {
            var rows = vault.Backups.All()
                .Where(b => File.Exists(b.OriginalPath))
                .GroupBy(b => b.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var path = g.Key;
                    var item = vault.Find(LibraryScanner.ComputeId(path));
                    return new
                    {
                        id = LibraryScanner.ComputeId(path),
                        fileName = Path.GetFileName(path),
                        kind = ItemKindText.KeyOf(item?.Kind ?? ItemKind.Other),
                        kindLabel = ItemKindText.LabelOf(item?.Kind ?? ItemKind.Other),
                        rootSource = (int)(item?.RootSource ?? LibrarySource.Normal),
                        lastModified = g.Max(b => b.SavedAt),
                        edits = g.Count(),
                    };
                })
                .OrderByDescending(r => r.lastModified)
                .Take(100)
                .ToList();
            return Json(new { rows });
        }));

        // ---------- 收纳入库（v0.7.3） ----------
        // 预扫描来源文件夹给出分类预览；确认后批量复制（默认，源不动）进局外库根的类型子目录。
        // 酒馆库根禁止作为目标（只读托管）；archive/other 不收纳（报告中建议跳过）。
        app.MapPost("/api/collect/preview", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var source = body?["source"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(source)) return Err("来源目录为空", 400);
            var full = Path.GetFullPath(source);
            if (!Directory.Exists(full)) return Err("来源文件夹不存在", 400);

            var candidates = CollectScanner.Scan(full);
            var groups = candidates
                .Where(c => CollectScanner.SubdirFor(c.Kind) is not null)
                .GroupBy(c => c.Kind)
                .Select(g => new
                {
                    kind = ItemKindText.KeyOf(g.Key),
                    label = ItemKindText.LabelOf(g.Key),
                    subdir = CollectScanner.SubdirFor(g.Key),
                    files = g.Select(c => new
                    {
                        path = c.RelativePath,
                        name = c.FileName,
                        size = c.SizeBytes,
                    }).ToList(),
                })
                .OrderBy(g => g.kind)
                .ToList();
            var skipped = candidates
                .Where(c => CollectScanner.SubdirFor(c.Kind) is null)
                .Select(c => new { path = c.RelativePath, name = c.FileName })
                .ToList();
            return Json(new { source = full, groups, skipped });
        }));

        app.MapPost("/api/collect", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var source = body?["source"]?.GetValue<string>();
            var rootPath = body?["root"]?.GetValue<string>();
            var move = body?["move"]?.GetValue<bool>() ?? false;
            if (string.IsNullOrWhiteSpace(source)) return Err("来源目录为空", 400);
            var full = Path.GetFullPath(source);
            if (!Directory.Exists(full)) return Err("来源文件夹不存在", 400);

            var root = vault.Settings.LibraryRoots.FirstOrDefault(r =>
                string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(rootPath ?? ""), StringComparison.OrdinalIgnoreCase));
            if (root is null) return Err("目标库根未登记", 400);
            if (root.Source != LibrarySource.Normal) return Err("酒馆库根为只读托管，不能作为收纳目标", 400);

            var candidates = CollectScanner.Scan(full).ToDictionary(c => c.RelativePath, StringComparer.OrdinalIgnoreCase);
            var requested = body?["files"] as JsonArray;
            List<CollectCandidate> picked;
            if (requested is not null)
            {
                picked = [];
                foreach (var node in requested.OfType<JsonValue>())
                {
                    var rel = node.GetValue<string>();
                    if (!candidates.TryGetValue(rel, out var c))
                        return Err($"文件清单包含未知条目：{rel}", 400);
                    picked.Add(c);
                }
            }
            else
            {
                picked = [.. candidates.Values.Where(c => CollectScanner.SubdirFor(c.Kind) is not null)];
            }

            var report = new List<object>();
            var warnings = new List<string>();
            int copied = 0;
            foreach (var c in picked)
            {
                var subdir = CollectScanner.SubdirFor(c.Kind);
                if (subdir is null)
                {
                    report.Add(new { file = c.RelativePath, status = "skipped" });
                    continue;
                }
                try
                {
                    var targetDir = Path.Combine(root.Path, subdir);
                    Directory.CreateDirectory(targetDir);
                    var dest = FileOperations.UniqueDestinationPath(targetDir, c.FileName);
                    File.Copy(c.FullPath, dest);
                    vault.UpsertItem(dest);
                    copied++;
                    if (move)
                    {
                        try
                        {
                            FileOperations.Recycle(c.FullPath);
                            report.Add(new { file = c.RelativePath, status = "moved", dest = Path.GetFileName(dest) });
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"{c.FileName} 已复制但源文件删除失败：{ex.Message}");
                            report.Add(new { file = c.RelativePath, status = "copied", dest = Path.GetFileName(dest) });
                        }
                    }
                    else
                    {
                        report.Add(new { file = c.RelativePath, status = "copied", dest = Path.GetFileName(dest) });
                    }
                }
                catch (Exception ex)
                {
                    report.Add(new { file = c.RelativePath, status = "failed", error = ex.Message });
                }
            }
            AppLog.Info($"收纳入库：{full} → {root.Path}，复制 {copied}/{picked.Count}（move={move}）");
            return Json(new { ok = true, copied, total = picked.Count, warnings, report });
        }));

        // ---------- 新建文件（v0.6.0） ----------
        // body: { kind, name, root? } —— 按空白模板创建文件并登记索引。
        // 护栏哲学：仅普通库根可新建，酒馆来源的文件由酒馆按路径/文件名引用，禁止从这里写入。
        app.MapPost("/api/items/create", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var kindKey = body?["kind"]?.GetValue<string>();
            var name = body?["name"]?.GetValue<string>();

            // kind 必须是可新建类型：ItemKindText 键能解析，且 ExtensionFor 非 null（archive/other 拒绝）
            if (kindKey is null || !ItemKindText.All.Any(a => a.Key == kindKey) ||
                ContentTemplates.ExtensionFor(ItemKindText.All.First(a => a.Key == kindKey).Kind) is not { } ext)
                return Err("该类型不支持新建", 400);
            var kind = ItemKindText.All.First(a => a.Key == kindKey).Kind;

            if (string.IsNullOrWhiteSpace(name)) return Err("名称不能为空", 400);
            // DisplayName/名称不可信内容统一清洗（同内嵌书导出），杜绝经名称拼出库外路径
            var safeName = FileOperations.SanitizeFileName(name);
            if (safeName.Length == 0) return Err("名称清洗后为空，请换个名字", 400);

            // 目标根：缺省 = 第一个普通库根；显式指定时必须是已注册且来源为普通（酒馆来源 400）
            LibraryRoot? target;
            if (string.IsNullOrWhiteSpace(body?["root"]?.GetValue<string>()))
            {
                target = vault.Settings.LibraryRoots.FirstOrDefault(r => r.Source == LibrarySource.Normal);
                if (target is null) return Err("没有可用的普通库根，请先在库设置中添加", 400);
            }
            else
            {
                var rootPath = body!["root"]!.GetValue<string>();
                target = vault.FindRoot(rootPath);
                if (target is null || target.Source != LibrarySource.Normal)
                    return Err("新建仅支持普通库根（酒馆来源的目录由酒馆管理）", 400);
            }

            // 重名处理：自实现" (n)" 序号后缀（2 起累加），与 GetSaveAsPath 的编号风格一致；
            // 比"-副本 时间戳"对新建场景更直观，故不复用 GetSaveAsPath
            var targetPath = Path.Combine(target.Path, safeName + ext);
            int n = 2;
            while (File.Exists(targetPath))
                targetPath = Path.Combine(target.Path, $"{safeName} ({n++}){ext}");

            // 写入：JSON 缩进 + UTF8 无 BOM（与既有写路径同款）；text 直接写字符串
            var content = kind == ItemKind.Text
                ? ContentTemplates.CreateText(kind, safeName) ?? ""
                : ContentTemplates.CreateJson(kind, safeName)?.ToJsonString(JsonOptions.WriteIndented) ?? "";
            AppLog.Info($"新建文件：{targetPath}");
            await File.WriteAllTextAsync(targetPath, content, new UTF8Encoding(false));

            // 新文件在已登记根内，UpsertItem 自动建条目（含类型识别）
            var item = vault.UpsertItem(targetPath);
            return item is null
                ? Err("文件已创建，但未能登记索引（目录可能已不在库根内）", 500)
                : Json(new { ok = true, id = item.Id, fileName = item.FileName });
        }));

        // ---------- 角色卡内嵌世界书（data.character_book） ----------
        app.MapGet("/api/cards/{id}/book", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            try
            {
                var card = CharacterCardFile.Load(item.FullPath) as JsonObject;
                if (card is null) return Err("无法解析角色卡", 400);
                var data = CharacterCardFile.GetDataNode(card);
                if (!CharacterBook.HasBook(data)) return Err("该卡片没有内置世界书", 404);

                var list = new JsonArray();
                foreach (var e in CharacterBook.ReadEntries(data["character_book"]!.AsObject()))
                {
                    list.Add(new JsonObject
                    {
                        ["key"] = e.MapKey,
                        ["data"] = e.St.DeepClone(),
                        ["raw"] = e.Raw?.DeepClone(), // Spec 原条目，保存时原样回传以合并编辑
                    });
                }
                return Json(new { fileName = item.FileName, entries = list });
            }
            catch (Exception ex)
            {
                // v0.5.2 修复 P2-2：原始消息可能含绝对路径，只回通用文案；完整异常落日志
                AppLog.Error($"读取内置世界书失败：{item.FullPath}", ex);
                return Err("读取失败，文件可能已损坏", 500);
            }
        }));

        // body: { entries: [{ key, data, raw? }], expectedModified? } —— raw 为 Spec 原条目时合并编辑，否则按 ST 格式写入
        app.MapPut("/api/cards/{id}/book", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            if (TavernEditGuard(item) is { } g2) return g2;

            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["entries"] is not JsonArray list) return Err("请求体格式错误", 400);
            if (CheckNotModified(item, body) is { } conflict) return conflict;

            var card = CharacterCardFile.Load(item.FullPath) as JsonObject;
            if (card is null) return Err("无法解析原角色卡", 400);
            var data = CharacterCardFile.GetDataNode(card);

            var book = data["character_book"] as JsonObject;
            if (book is null)
            {
                book = CharacterBook.CreateBook();
                data["character_book"] = book;
            }

            var entries = new List<CharacterBook.BookEntry>();
            foreach (var node in list)
            {
                if (node is not JsonObject e) continue;
                entries.Add(new CharacterBook.BookEntry
                {
                    MapKey = e["key"]?.GetValue<string>() ?? entries.Count.ToString(),
                    St = e["data"] as JsonObject ?? new JsonObject(),
                    Raw = (e["raw"] as JsonObject)?.DeepClone().AsObject(),
                });
            }
            CharacterBook.WriteEntries(book, entries);

            var warnings = new List<string>();
            AddWarnings(warnings, vault.BackupBeforeWrite(item.FullPath));
            CharacterCardFile.Save(item.FullPath, card);
            vault.UpsertItem(item.FullPath);
            return Json(new { ok = true, id = LibraryScanner.ComputeId(item.FullPath), count = entries.Count, warnings, modifiedAt = File.GetLastWriteTime(item.FullPath) });
        }));

        // ---------- 世界书编辑 ----------
        // v0.6.0：entries 容器形态（对象/数组）不可互换。
        // ST 内部世界书 entries 为对象（uid 键）；Spec V2 / NovelAI 导出为数组（条目 keys/enabled）。
        // 返回体新增 container: "object" | "array"；数组容器复用 CharacterBook 的 Spec→ST
        // 读取逻辑，条目附 raw（Spec 原条目）供写回时保形合并。前端默认对象场景不变。
        app.MapGet("/api/lore/{id}", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Lorebook) return Err("不是世界书", 400);
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(item.FullPath)) as JsonObject;
                switch (root?["entries"])
                {
                    case JsonObject entries:
                        {
                            var list = new JsonArray();
                            foreach (var (key, value) in entries)
                                list.Add(new JsonObject { ["key"] = key, ["data"] = value?.DeepClone() });
                            return Json(new { fileName = item.FileName, container = "object", entries = list });
                        }
                    case JsonArray:
                        {
                            var list = new JsonArray();
                            foreach (var e in CharacterBook.ReadEntries(root))
                            {
                                list.Add(new JsonObject
                                {
                                    ["key"] = e.MapKey,
                                    ["data"] = e.St.DeepClone(),
                                    ["raw"] = e.Raw?.DeepClone(), // Spec 原条目，保存时原样回传以合并编辑
                                });
                            }
                            return Json(new { fileName = item.FileName, container = "array", entries = list });
                        }
                    default:
                        return Err("缺少 entries 结构", 400);
                }
            }
            catch (Exception ex)
            {
                // v0.5.2 修复 P2-2：原始消息可能含绝对路径，只回通用文案；完整异常落日志
                AppLog.Error($"读取世界书失败：{item.FullPath}", ex);
                return Err("读取失败，文件可能已损坏", 500);
            }
        }));

        // body: { entries: [{ key, data, raw? }], container?: "array"|"object", expectedModified? }
        // container="array"：按数组容器写回，条目走 CharacterBook 保形合并（raw 未编辑字段原样保留），
        // 容器仍为数组；container="object"/缺省：维持整体重建对象 entries 的既有行为，其它顶层键保留。
        app.MapPut("/api/lore/{id}", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Lorebook) return Err("不是世界书", 400);
            if (TavernEditGuard(item) is { } g3) return g3;

            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["entries"] is not JsonArray list) return Err("请求体格式错误", 400);
            if (CheckNotModified(item, body) is { } conflict) return conflict;

            var root = JsonNode.Parse(File.ReadAllText(item.FullPath)) as JsonObject
                ?? throw new InvalidOperationException("原文件不是 JSON 对象");
            int count;

            if (body["container"]?.GetValue<string>() == "array")
            {
                // 保形写回：先固定数组容器形态，再交给 CharacterBook 逐条合并
                root["entries"] = new JsonArray();
                var entries = new List<CharacterBook.BookEntry>();
                foreach (var node in list)
                {
                    if (node is not JsonObject e) continue;
                    entries.Add(new CharacterBook.BookEntry
                    {
                        MapKey = e["key"]?.GetValue<string>() ?? entries.Count.ToString(),
                        St = e["data"] as JsonObject ?? new JsonObject(),
                        Raw = (e["raw"] as JsonObject)?.DeepClone().AsObject(),
                    });
                }
                CharacterBook.WriteEntries(root, entries);
                count = entries.Count;
            }
            else
            {
                var entries = new JsonObject();
                foreach (var node in list)
                {
                    if (node is not JsonObject e) continue;
                    var key = e["key"]?.GetValue<string>() ?? entries.Count.ToString();
                    entries[key] = e["data"]?.DeepClone() ?? new JsonObject();
                }
                root["entries"] = entries;
                count = entries.Count;
            }

            var warnings = new List<string>();
            AddWarnings(warnings, vault.BackupBeforeWrite(item.FullPath));
            await File.WriteAllTextAsync(item.FullPath, root.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
            vault.UpsertItem(item.FullPath);
            return Json(new { ok = true, count, warnings, modifiedAt = File.GetLastWriteTime(item.FullPath) });
        }));

        // ---------- 文本 / 原始 JSON 编辑 ----------
        app.MapGet("/api/text/{id}", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            if (!File.Exists(item.FullPath)) return Results.NotFound(); // v0.5.2：文件已被外部删除按 404 处理，而非读取抛异常
            if (!TextExtensions.Contains(Path.GetExtension(item.FullPath))) return Err("该文件类型不支持文本编辑", 400);
            if (item.SizeBytes > 20_000_000) return Err("文件过大", 400);
            return Json(new { content = File.ReadAllText(item.FullPath) });
        }));

        app.MapPut("/api/text/{id}", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            if (TavernEditGuard(item) is { } g4) return g4;
            if (!TextExtensions.Contains(Path.GetExtension(item.FullPath))) return Err("该文件类型不支持文本编辑", 400);

            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var content = body?["content"]?.GetValue<string>();
            if (content is null) return Err("请求体格式错误", 400);
            if (CheckNotModified(item, body) is { } conflict) return conflict;

            if (Path.GetExtension(item.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                try { _ = JsonNode.Parse(content); }
                catch (System.Text.Json.JsonException ex) { return Err($"JSON 校验失败：{ex.Message}", 400); }
            }

            var warnings = new List<string>();
            AddWarnings(warnings, vault.BackupBeforeWrite(item.FullPath));
            await File.WriteAllTextAsync(item.FullPath, content, new UTF8Encoding(false));
            vault.UpsertItem(item.FullPath);
            return Json(new { ok = true, warnings, modifiedAt = File.GetLastWriteTime(item.FullPath) });
        }));

        // ---------- 备份与还原 ----------
        app.MapGet("/api/items/{id}/backups", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            return Json(vault.Backups.List(item.FullPath));
        }));

        app.MapPost("/api/backups/{bid}/restore", (HttpContext ctx, string bid) => Handle(ctx, () =>
        {
            var path = vault.Backups.Restore(bid, out var restoreWarning) ?? throw new InvalidOperationException("备份不存在或已损坏");
            var warnings = new List<string>();
            AddWarnings(warnings, restoreWarning);
            vault.UpsertItem(path);
            return Json(new { ok = true, id = LibraryScanner.ComputeId(path), warnings });
        }));

        app.MapDelete("/api/backups/{bid}", (HttpContext ctx, string bid) => Handle(ctx, () =>
            vault.Backups.Delete(bid) ? Json(new { ok = true }) : Err("备份不存在", 404)));

        app.MapGet("/api/backups/stats", (HttpContext ctx) => Handle(ctx, () =>
        {
            var (count, bytes) = vault.Backups.Stats();
            return Json(new
            {
                count,
                bytes,
                autoBackup = vault.Settings.AutoBackup,
                maxPerFile = vault.Settings.MaxBackupsPerFile,
                dir = vault.Backups.Dir,
                defaultDir = Path.Combine(vault.DataDir, "backups"),
            });
        }));

        app.MapPost("/api/settings/backup", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["autoBackup"] is JsonValue ab) vault.Settings.AutoBackup = ab.GetValue<bool>();
            if (body?["maxPerFile"] is JsonValue mx && mx.TryGetValue<int>(out var max))
                vault.Settings.MaxBackupsPerFile = Math.Clamp(max, 1, 50);
            vault.Backups.MaxPerFile = Math.Clamp(vault.Settings.MaxBackupsPerFile, 1, 50);
            if (body?["backupDir"] is JsonValue bd)
            {
                var dir = bd.GetValue<string>().Trim();
                try
                {
                    if (dir.Length == 0) vault.SetBackupRoot(null);
                    else if (!Path.IsPathRooted(dir)) return Err("备份位置必须是绝对路径", 400);
                    else vault.SetBackupRoot(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return Err($"无法使用该备份位置：{ex.Message}", 400);
                }
            }
            vault.SaveSettings();
            return Json(new
            {
                ok = true,
                autoBackup = vault.Settings.AutoBackup,
                maxPerFile = vault.Settings.MaxBackupsPerFile,
                dir = vault.Backups.Dir,
            });
        }));

        // ---------- 文件操作 ----------
        app.MapPost("/api/items/{id}/favorite", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var fav = (await JsonNode.ParseAsync(req.Body))?["fav"]?.GetValue<bool>() ?? false;
            return vault.SetFavorite(id, fav) ? Json(new { ok = true }) : Err("条目不存在", 404);
        }));

        app.MapPost("/api/items/{id}/tags", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var arr = (await JsonNode.ParseAsync(req.Body))?["tags"] as JsonArray ?? new JsonArray();
            var tags = arr.OfType<JsonValue>().Select(v => v.GetValue<string>() ?? "").ToList();
            return vault.SetUserTags(id, tags) ? Json(new { ok = true }) : Err("条目不存在", 404);
        }));

        app.MapPost("/api/items/{id}/rename", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var name = body?["name"]?.GetValue<string>();
            var force = body?["force"]?.GetValue<bool>() ?? false;
            if (item.RootSource != LibrarySource.Normal && !force)
                return Err("酒馆来源的角色卡不允许重命名（聊天通过文件名引用）", 403);
            var userData = vault.GetUserData(id); // Id 随路径变化，先取快照
            var warnings = new List<string>();
            AddWarnings(warnings, vault.BackupBeforeWrite(item.FullPath));
            var oldPath = item.FullPath;
            var newPath = FileOperations.Rename(item, name ?? "");
            vault.RemoveItem(oldPath);
            vault.UpsertItem(newPath);
            var newId = LibraryScanner.ComputeId(newPath);
            vault.SetUserData(newId, userData.Favorite, userData.Tags);
            return Json(new { ok = true, id = newId, warnings });
        }));

        app.MapPost("/api/items/{id}/move", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var root = body?["root"]?.GetValue<string>() ?? item.RootPath;
            var dir = body?["dir"]?.GetValue<string>() ?? "";
            var force = body?["force"]?.GetValue<bool>() ?? false;
            if (item.RootSource != LibrarySource.Normal && !force)
                return Err("酒馆来源的文件不允许移出原根（聊天通过路径引用）", 403);
            FileOperations.GuardUnderRoots(root, vault.Settings.LibraryRoots.Select(r => r.Path));
            var userData = vault.GetUserData(id); // Id 随路径变化，先取快照
            // v0.5.2 修复 N4：移动前先备份原文件（与 rename 端点同款）。
            // 必须在移动前收集——备份的是移动前的原文件。
            var warnings = new List<string>();
            AddWarnings(warnings, vault.BackupBeforeWrite(item.FullPath));
            var oldPath = item.FullPath;
            var newPath = FileOperations.Move(item, root, dir);
            vault.RemoveItem(oldPath);
            vault.UpsertItem(newPath);
            var newId = LibraryScanner.ComputeId(newPath);
            vault.SetUserData(newId, userData.Favorite, userData.Tags);
            return Json(new { ok = true, id = newId, warnings });
        }));

        app.MapPost("/api/items/{id}/delete", (HttpContext ctx, string id) => Handle(ctx, () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            FileOperations.Recycle(item.FullPath);
            vault.RemoveItem(item.FullPath);
            return Json(new { ok = true });
        }));

        app.MapPost("/api/reveal", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body);
            // {dataDir:true}：打开数据目录（设置/索引/备份/日志所在，v0.7.1 起在库设置中展示）
            if (body?["dataDir"] is JsonValue dv && dv.TryGetValue<bool>(out var wantDir) && wantDir)
            {
                FileOperations.RevealInExplorer(vault.DataDir);
                return Json(new { ok = true });
            }
            var item = vault.Find(body?["id"]?.GetValue<string>() ?? "");
            if (item is null) return Results.NotFound();
            FileOperations.RevealInExplorer(item.FullPath);
            return Json(new { ok = true });
        }));

        // ---------- 设置 ----------
        app.MapPost("/api/roots", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var path = body?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path)) return Err("路径为空", 400);
            var source = ParseSource(body?["source"]?.GetValue<string>());
            vault.AddRoot(new LibraryRoot { Path = path, Source = source });
            watcher.RefreshRoots(); // 新库根纳入监视（v0.7.2）
            vault.Rescan();
            return Json(new { ok = true, roots = SerializeRoots(vault) });
        }));

        app.MapDelete("/api/roots", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var path = (await JsonNode.ParseAsync(req.Body))?["path"]?.GetValue<string>();
            vault.RemoveRoot(path ?? "");
            watcher.RefreshRoots();
            vault.Rescan();
            return Json(new { ok = true, roots = SerializeRoots(vault) });
        }));

        // ---------- 酒馆检测 ----------
        app.MapPost("/api/tavern/detect", (HttpContext ctx) => Handle(ctx, () =>
        {
            var found = TavernDetector.DetectAll()
                .Select(d => new
                {
                    source = SourceKey(d.source),
                    label = d.label,
                    subdirs = d.subdirs,
                }).ToList();
            return Json(new { found });
        }));

        app.MapPost("/api/tavern/connect", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var sourceKey = body?["source"]?.GetValue<string>() ?? "";
            var source = ParseSource(sourceKey);
            if (source == LibrarySource.Normal)
                return Err("无效的酒馆来源", 400);

            var detected = TavernDetector.DetectAll();
            var match = detected.FirstOrDefault(d => d.source == source);
            if (match == default)
                return Err("未检测到该酒馆安装", 404);

            var existing = new HashSet<string>(vault.Settings.LibraryRoots.Select(r => r.Path));
            var roots = TavernDetector.BuildRoots(match.baseDir, source);
            var added = 0;
            foreach (var r in roots)
            {
                if (existing.Add(r.Path))
                {
                    vault.AddRoot(r);
                    added++;
                }
            }
            vault.Rescan();
            return Json(new { ok = true, added, roots = SerializeRoots(vault) });
        }));

        app.MapPost("/api/pick-folder", (HttpContext ctx) => Handle(ctx, () =>
        {
            if (headless) return Err("无窗口模式下不支持文件夹选择框", 400);
            var picked = FolderPicker.Pick();
            return picked is null ? Json<object?>(null) : Json(new { path = picked });
        }));

        app.MapGet("/api/categories", (HttpContext ctx) => Handle(ctx, () =>
        {
            var dirs = vault.Query(new QueryParams())
                .GroupBy(i => (i.RootPath, i.RelativeDir))
                .Select(g => new
                {
                    root = g.Key.RootPath,
                    dir = g.Key.RelativeDir,
                    count = g.Count(),
                })
                .OrderBy(g => g.root).ThenBy(g => g.dir)
                .ToList();
            return Json(dirs);
        }));
    }

    // ---- 统一包装：强制返回 IResult，统一错误处理 ----
    private static IResult Json<T>(T value) => Results.Json(value);

    private static IResult Handle(HttpContext ctx, Func<IResult> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or ArgumentException
                                       or InvalidOperationException or Win32Exception
                                       or OperationCanceledException) // v0.5.2 补：客户端断开（P2-2）
        {
            AppLog.Error($"{ctx.Request.Method} {ctx.Request.Path} 失败", ex);
            return Err(ex.Message, 400);
        }
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or ArgumentException
                                       or InvalidOperationException or Win32Exception
                                       or System.Text.Json.JsonException
                                       or OperationCanceledException) // v0.5.2 补：客户端断开（P2-2）
        {
            AppLog.Error($"{ctx.Request.Method} {ctx.Request.Path} 失败", ex);
            return Err(ex.Message, 400);
        }
    }

    /// <summary>收集写路径警告（null 忽略），同时落 WARN 日志。</summary>
    private static void AddWarnings(List<string> warnings, string? w)
    {
        if (w is null) return;
        warnings.Add(w);
        AppLog.Warn(w);
    }

    /// <summary>
    /// v0.5.2 修复 P1-2：合并多条启动期告警（设置损坏 / 备份记录缺席），
    /// 拼接后统一经 /api/meta.settingsWarning 外显（可含多条，以"；"分隔）；无告警时为 null。
    /// </summary>
    private static string? CombinedWarning(params string?[] warnings)
    {
        var parts = warnings.Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
        return parts.Length == 0 ? null : string.Join("；", parts);
    }

    /// <summary>
    /// 编辑并发防护：请求体带 expectedModified（读取条目时的 modifiedAt 原样回传）时，
    /// 校验文件当前修改时间是否仍一致；不匹配返回 409，防止两个编辑窗口互相覆盖（后写胜出丢失编辑）。
    /// 未携带该字段则跳过校验（旧脚本兼容）。
    /// </summary>
    private static IResult? CheckNotModified(LibraryItem item, JsonObject? body)
    {
        var expected = body?["expectedModified"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(expected)) return null;
        if (!DateTime.TryParse(expected, out var want)) return null;
        var current = File.GetLastWriteTime(item.FullPath);
        if (Math.Abs((current - want).TotalSeconds) > 1.0)
            return Err("文件已被外部程序或其它窗口修改，本次未写入；请重新打开该条目后再保存", 409);
        return null;
    }

    private static IResult Err(string message, int code) =>
        Results.Json(new { error = message }, statusCode: code);

    // ---- 酒馆来源就地编辑守卫（v0.7.1） ----
    // 实测确认：酒馆不会实时读取外部修改，角色卡等还被酒馆常驻内存缓存，
    // 界面操作会用旧数据回写覆盖外部编辑（冷生效同样不可靠）。
    // 酒馆资源的可靠编辑路径 = 「导出副本」到局外库根 → 编辑副本 → 酒馆自带导入写回。
    private static readonly string TavernEditBlockedMsg =
        "酒馆来源文件不支持就地编辑：酒馆不会实时读取外部修改，且界面操作可能用内存中的旧数据回写覆盖。"
        + "请在详情页使用「导出副本」到局外存储后编辑，再通过酒馆自带的导入功能写回。";

    /// <summary>酒馆来源（ST/TT）条目禁止就地编辑，返回 403；局外来源返回 null 放行。</summary>
    private static IResult? TavernEditGuard(LibraryItem item) =>
        item.RootSource == LibrarySource.Normal ? null : Err(TavernEditBlockedMsg, 403);


    // ---- 库根序列化 / 来源解析 ----
    private static List<object> SerializeRoots(Vault v) =>
        v.Settings.LibraryRoots.Select(r => (object)new
        {
            path = r.Path,
            source = SourceKey(r.Source),
            count = v.Query(new QueryParams { RootPath = r.Path }).Count,
        }).ToList();

    private static string SourceKey(LibrarySource s) => s switch
    {
        LibrarySource.TavernST => "tavernST",
        LibrarySource.TavernTT => "tavernTT",
        _ => "normal",
    };

    private static LibrarySource ParseSource(string? key) => key switch
    {
        "tavernST" => LibrarySource.TavernST,
        "tavernTT" => LibrarySource.TavernTT,
        _ => LibrarySource.Normal,
    };
}
