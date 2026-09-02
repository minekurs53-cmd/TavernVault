using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;
using TavernVault.App.Services;
using TavernVault.Core.Cards;
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

        var vault = new Vault(new SettingsStore(ResolveDataDir(args)));
        AppLog.Init(vault.DataDir);
        AppLog.Info($"启动 v{typeof(ApiServer).Assembly.GetName().Version?.ToString(4)}（数据目录 {vault.DataDir}）");
        if (vault.SettingsWarning is { } warn) AppLog.Warn(warn);
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

        MapApi(app, vault, thumbs, headless);
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

    private static void MapApi(WebApplication app, Vault vault, ThumbnailService thumbs, bool headless)
    {
        // ---------- 元信息 / 扫描 ----------
        app.MapGet("/api/meta", () =>
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
                settingsWarning = vault.SettingsWarning,
                version = typeof(ApiServer).Assembly.GetName().Version?.ToString(4),
            });
        });

        app.MapPost("/api/rescan", (HttpContext ctx) => Handle(ctx, () =>
        {
            int count = vault.Rescan();
            return Json(new { count });
        }));

        // ---------- 条目查询 ----------
        app.MapGet("/api/items", (string? kind, string? q, string? tag, bool? fav, string? sort, string? dir, string? root, string? source) =>
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
        });

        app.MapGet("/api/items/{id}", (string id) =>
            vault.Find(id) is { } item ? Json(item) : Err("条目不存在", 404));

        // ---------- 缩略图 / 原图 ----------
        app.MapGet("/api/thumb/{id}", async (string id) =>
        {
            var item = vault.Find(id);
            if (item is null || !item.HasEmbeddedCard) return Results.NotFound();
            var path = await thumbs.GetAsync(item);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        app.MapGet("/api/image/{id}", (string id) =>
        {
            var item = vault.Find(id);
            if (item is null || !item.HasEmbeddedCard) return Results.NotFound();
            return Results.File(item.FullPath, "image/png", enableRangeProcessing: true);
        });

        // ---------- 角色卡编辑 ----------
        app.MapGet("/api/cards/{id}", (string id) =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            try
            {
                var card = CharacterCardFile.Load(item.FullPath);
                if (card is null) return Err("无法解析角色卡数据", 400);
                return Json(new { fileName = item.FileName, card });
            }
            catch (Exception ex) { return Err(ex.Message, 500); }
        });

        // body: { fields: {...}, alternateGreetings: [...], tags: [...], expectedModified? } —— 服务端合并保存，避免整卡回传
        app.MapPut("/api/cards/{id}", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);

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

        // 世界书：编辑后的条目写入新文件（其它顶层键保留）
        app.MapPost("/api/lore/{id}/saveas", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Lorebook) return Err("不是世界书", 400);
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["entries"] is not JsonArray list) return Err("请求体格式错误", 400);

            var root = JsonNode.Parse(File.ReadAllText(item.FullPath)) as JsonObject
                ?? throw new InvalidOperationException("原文件不是 JSON 对象");
            var entries = new JsonObject();
            foreach (var node in list)
            {
                if (node is not JsonObject e) continue;
                entries[e["key"]?.GetValue<string>() ?? entries.Count.ToString()] = e["data"]?.DeepClone() ?? new JsonObject();
            }
            root["entries"] = entries;

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

        // ---------- 角色卡内嵌世界书（data.character_book） ----------
        app.MapGet("/api/cards/{id}/book", (string id) =>
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
            catch (Exception ex) { return Err(ex.Message, 500); }
        });

        // body: { entries: [{ key, data, raw? }], expectedModified? } —— raw 为 Spec 原条目时合并编辑，否则按 ST 格式写入
        app.MapPut("/api/cards/{id}/book", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);

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
        app.MapGet("/api/lore/{id}", (string id) =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Lorebook) return Err("不是世界书", 400);
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(item.FullPath)) as JsonObject;
                if (root?["entries"] is not JsonObject entries) return Err("缺少 entries 结构", 400);

                var list = new JsonArray();
                foreach (var (key, value) in entries)
                    list.Add(new JsonObject { ["key"] = key, ["data"] = value?.DeepClone() });
                return Json(new { fileName = item.FileName, entries = list });
            }
            catch (Exception ex) { return Err(ex.Message, 500); }
        });

        // body: { entries: [{ key, data }], expectedModified? } —— 整体重建 entries，其它顶层键保留
        app.MapPut("/api/lore/{id}", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Lorebook) return Err("不是世界书", 400);

            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["entries"] is not JsonArray list) return Err("请求体格式错误", 400);
            if (CheckNotModified(item, body) is { } conflict) return conflict;

            var root = JsonNode.Parse(File.ReadAllText(item.FullPath)) as JsonObject
                ?? throw new InvalidOperationException("原文件不是 JSON 对象");
            var entries = new JsonObject();
            foreach (var node in list)
            {
                if (node is not JsonObject e) continue;
                var key = e["key"]?.GetValue<string>() ?? entries.Count.ToString();
                entries[key] = e["data"]?.DeepClone() ?? new JsonObject();
            }
            root["entries"] = entries;

            var warnings = new List<string>();
            AddWarnings(warnings, vault.BackupBeforeWrite(item.FullPath));
            await File.WriteAllTextAsync(item.FullPath, root.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
            vault.UpsertItem(item.FullPath);
            return Json(new { ok = true, count = entries.Count, warnings, modifiedAt = File.GetLastWriteTime(item.FullPath) });
        }));

        // ---------- 文本 / 原始 JSON 编辑 ----------
        app.MapGet("/api/text/{id}", (string id) =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            if (!TextExtensions.Contains(Path.GetExtension(item.FullPath))) return Err("该文件类型不支持文本编辑", 400);
            if (item.SizeBytes > 20_000_000) return Err("文件过大", 400);
            return Json(new { content = File.ReadAllText(item.FullPath) });
        });

        app.MapPut("/api/text/{id}", async (HttpContext ctx, string id, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
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
        app.MapGet("/api/items/{id}/backups", (string id) =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            return Json(vault.Backups.List(item.FullPath));
        });

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

        app.MapGet("/api/backups/stats", () =>
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
        });

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
            var oldPath = item.FullPath;
            var newPath = FileOperations.Move(item, root, dir);
            vault.RemoveItem(oldPath);
            vault.UpsertItem(newPath);
            var newId = LibraryScanner.ComputeId(newPath);
            vault.SetUserData(newId, userData.Favorite, userData.Tags);
            return Json(new { ok = true, id = newId });
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
            vault.Rescan();
            return Json(new { ok = true, roots = SerializeRoots(vault) });
        }));

        app.MapDelete("/api/roots", async (HttpContext ctx, HttpRequest req) => await HandleAsync(ctx, async () =>
        {
            var path = (await JsonNode.ParseAsync(req.Body))?["path"]?.GetValue<string>();
            vault.RemoveRoot(path ?? "");
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

        app.MapGet("/api/categories", () =>
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
        });
    }

    // ---- 统一包装：强制返回 IResult，统一错误处理 ----
    private static IResult Json<T>(T value) => Results.Json(value);

    private static IResult Handle(HttpContext ctx, Func<IResult> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or ArgumentException
                                       or InvalidOperationException or Win32Exception)
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
                                       or System.Text.Json.JsonException)
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
