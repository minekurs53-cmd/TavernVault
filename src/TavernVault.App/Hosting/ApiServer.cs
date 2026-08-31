using System.ComponentModel;
using System.IO;
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

/// <summary>Kestrel 本地服务：静态前端 + REST API。只绑定 127.0.0.1。</summary>
public static class ApiServer
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".json", ".js", ".mjs", ".ts", ".py", ".qs", ".ps1", ".bat", ".yaml", ".yml", ".md", ".txt", ".css", ".html", ".log" };

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        var port = Array.Find(args, a => a.StartsWith("--port="));
        builder.WebHost.UseUrls(port is null ? "http://127.0.0.1:0" : $"http://127.0.0.1:{port[7..]}");

        string? dataDir = Array.Find(args, a => a.StartsWith("--data=")) is { } d ? d[7..] : null;
        var vault = new Vault(new SettingsStore(dataDir));
        EnsureDefaultRoot(vault);
        var thumbs = new ThumbnailService();
        var headless = args.Contains("--server");

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // 本地单用户服务：始终重校验，避免更新前端文件后被缓存卡住
            OnPrepareResponse = ctx =>
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate",
        });

        MapApi(app, vault, thumbs, headless);
        return app;
    }

    /// <summary>首次运行时把常见的酒馆资源目录作为默认库（存在才加）。</summary>
    private static void EnsureDefaultRoot(Vault vault)
    {
        if (vault.Settings.LibraryRoots.Count > 0) return;
        var candidates = new[]
        {
            @"D:\agent\酒馆PR",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "酒馆PR"),
        };
        foreach (var guess in candidates)
        {
            if (!Directory.Exists(guess)) continue;
            vault.AddRoot(guess);
            break;
        }
    }

    private static void MapApi(WebApplication app, Vault vault, ThumbnailService thumbs, bool headless)
    {
        // ---------- 元信息 / 扫描 ----------
        app.MapGet("/api/meta", () =>
        {
            var (tags, total) = vault.AllUserTags();
            var kinds = ItemKindText.All
                .Select(a => new
                {
                    kind = ItemKindText.KeyOf(a.Kind),
                    label = a.Label,
                    count = vault.Query(new QueryParams { Kind = a.Kind }).Count,
                })
                .ToList();
            return Json(new
            {
                total,
                kinds,
                userTags = tags,
                roots = SerializeRoots(vault),
                lastScanAt = vault.LastScanAt,
                version = typeof(ApiServer).Assembly.GetName().Version?.ToString(3),
            });
        });

        app.MapPost("/api/rescan", () => Handle(() =>
        {
            int count = vault.Rescan();
            return Json(new { count });
        }));

        // ---------- 条目查询 ----------
        app.MapGet("/api/items", (string? kind, string? q, string? tag, bool? fav, string? sort, string? dir, string? root) =>
        {
            var p = new QueryParams
            {
                Kind = kind is { Length: > 0 } && Enum.TryParse<ItemKind>(kind, true, out var k) ? k : null,
                Search = q,
                UserTag = tag,
                Favorite = fav,
                Sort = sort ?? "name",
                Dir = dir,
                RootPath = root,
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

        // body: { fields: {...}, alternateGreetings: [...], tags: [...] } —— 服务端合并保存，避免整卡回传
        app.MapPut("/api/cards/{id}", async (string id, HttpRequest req) => await HandleAsync(async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);

            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body is null) return Err("请求体格式错误", 400);

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

            vault.BackupBeforeWrite(item.FullPath);
            CharacterCardFile.Save(item.FullPath, card);
            vault.Rescan();
            return Json(new { ok = true, id = LibraryScanner.ComputeId(item.FullPath) });
        }));

        // ---------- 另存为（自动命名副本） ----------
        // 卡片：当前编辑内容写入新文件（PNG 先复制原图再重新内嵌，图像保留）
        app.MapPost("/api/cards/{id}/saveas", async (string id, HttpRequest req) => await HandleAsync(async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var cardNode = body?["card"] as JsonObject;
            if (cardNode is null) return Err("请求体格式错误", 400);

            var newPath = FileOperations.GetSaveAsPath(item.FullPath);
            if (item.HasEmbeddedCard && item.FullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                File.Copy(item.FullPath, newPath); // 复制原图，再写入编辑后的卡片数据
            await File.WriteAllTextAsync(newPath,
                cardNode.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
            CharacterCardFile.Save(newPath, cardNode);
            vault.Rescan();
            var nid = LibraryScanner.ComputeId(newPath);
            return Json(new { ok = true, id = nid, fileName = Path.GetFileName(newPath) });
        }));

        // 世界书：编辑后的条目写入新文件（其它顶层键保留）
        app.MapPost("/api/lore/{id}/saveas", async (string id, HttpRequest req) => await HandleAsync(async () =>
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
            vault.Rescan();
            return Json(new { ok = true, id = LibraryScanner.ComputeId(newPath), fileName = Path.GetFileName(newPath) });
        }));

        // 内嵌世界书：导出为独立世界书（ST dict 格式）
        app.MapPost("/api/cards/{id}/book/saveas", async (string id, HttpRequest req) => await HandleAsync(async () =>
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
            var newPath = FileOperations.GetSaveAsPath(Path.Combine(dir, displayName + ".json"));
            var doc = new JsonObject { ["entries"] = entries };
            await File.WriteAllTextAsync(newPath, doc.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
            vault.Rescan();
            return Json(new { ok = true, id = LibraryScanner.ComputeId(newPath), fileName = Path.GetFileName(newPath) });
        }));

        // 文本/原始 JSON：内容写入新文件（.json 校验）
        app.MapPost("/api/text/{id}/saveas", async (string id, HttpRequest req) => await HandleAsync(async () =>
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
            vault.Rescan();
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

        // body: { entries: [{ key, data, raw? }] } —— raw 为 Spec 原条目时合并编辑，否则按 ST 格式写入
        app.MapPut("/api/cards/{id}/book", async (string id, HttpRequest req) => await HandleAsync(async () =>
        {
            var item = vault.Find(id);
            if (item is null || item.Kind != ItemKind.Character) return Err("不是角色卡", 400);

            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            if (body?["entries"] is not JsonArray list) return Err("请求体格式错误", 400);

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

            vault.BackupBeforeWrite(item.FullPath);
            CharacterCardFile.Save(item.FullPath, card);
            vault.Rescan();
            return Json(new { ok = true, id = LibraryScanner.ComputeId(item.FullPath), count = entries.Count });
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

        // body: { entries: [{ key, data }] } —— 整体重建 entries，其它顶层键保留
        app.MapPut("/api/lore/{id}", async (string id, HttpRequest req) => await HandleAsync(async () =>
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
                var key = e["key"]?.GetValue<string>() ?? entries.Count.ToString();
                entries[key] = e["data"]?.DeepClone() ?? new JsonObject();
            }
            root["entries"] = entries;

            vault.BackupBeforeWrite(item.FullPath);
            await File.WriteAllTextAsync(item.FullPath, root.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
            vault.Rescan();
            return Json(new { ok = true, count = entries.Count });
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

        app.MapPut("/api/text/{id}", async (string id, HttpRequest req) => await HandleAsync(async () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            if (!TextExtensions.Contains(Path.GetExtension(item.FullPath))) return Err("该文件类型不支持文本编辑", 400);

            var content = (await JsonNode.ParseAsync(req.Body))?["content"]?.GetValue<string>();
            if (content is null) return Err("请求体格式错误", 400);

            if (Path.GetExtension(item.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                try { _ = JsonNode.Parse(content); }
                catch (System.Text.Json.JsonException ex) { return Err($"JSON 校验失败：{ex.Message}", 400); }
            }

            vault.BackupBeforeWrite(item.FullPath);
            await File.WriteAllTextAsync(item.FullPath, content, new UTF8Encoding(false));
            vault.Rescan();
            return Json(new { ok = true });
        }));

        // ---------- 备份与还原 ----------
        app.MapGet("/api/items/{id}/backups", (string id) =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            return Json(vault.Backups.List(item.FullPath));
        });

        app.MapPost("/api/backups/{bid}/restore", (string bid) => Handle(() =>
        {
            var path = vault.Backups.Restore(bid) ?? throw new InvalidOperationException("备份不存在或已损坏");
            vault.Rescan();
            return Json(new { ok = true, id = LibraryScanner.ComputeId(path) });
        }));

        app.MapDelete("/api/backups/{bid}", (string bid) => Handle(() =>
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

        app.MapPost("/api/settings/backup", async (HttpRequest req) => await HandleAsync(async () =>
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
        app.MapPost("/api/items/{id}/favorite", async (string id, HttpRequest req) => await HandleAsync(async () =>
        {
            var fav = (await JsonNode.ParseAsync(req.Body))?["fav"]?.GetValue<bool>() ?? false;
            return vault.SetFavorite(id, fav) ? Json(new { ok = true }) : Err("条目不存在", 404);
        }));

        app.MapPost("/api/items/{id}/tags", async (string id, HttpRequest req) => await HandleAsync(async () =>
        {
            var arr = (await JsonNode.ParseAsync(req.Body))?["tags"] as JsonArray ?? new JsonArray();
            var tags = arr.OfType<JsonValue>().Select(v => v.GetValue<string>() ?? "").ToList();
            return vault.SetUserTags(id, tags) ? Json(new { ok = true }) : Err("条目不存在", 404);
        }));

        app.MapPost("/api/items/{id}/rename", async (string id, HttpRequest req) => await HandleAsync(async () =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var name = body?["name"]?.GetValue<string>();
            var force = body?["force"]?.GetValue<bool>() ?? false;
            if (item.RootSource != LibrarySource.Normal && !force)
                return Err("酒馆来源的角色卡不允许重命名（聊天通过文件名引用）", 403);
            var userData = vault.GetUserData(id); // Id 随路径变化，先取快照
            vault.BackupBeforeWrite(item.FullPath);
            var newPath = FileOperations.Rename(item, name ?? "");
            vault.Rescan();
            var newId = LibraryScanner.ComputeId(newPath);
            vault.SetUserData(newId, userData.Favorite, userData.Tags);
            return Json(new { ok = true, id = newId });
        }));

        app.MapPost("/api/items/{id}/move", async (string id, HttpRequest req) => await HandleAsync(async () =>
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
            var newPath = FileOperations.Move(item, root, dir);
            vault.Rescan();
            var newId = LibraryScanner.ComputeId(newPath);
            vault.SetUserData(newId, userData.Favorite, userData.Tags);
            return Json(new { ok = true, id = newId });
        }));

        app.MapPost("/api/items/{id}/delete", (string id) => Handle(() =>
        {
            var item = vault.Find(id);
            if (item is null) return Results.NotFound();
            FileOperations.Recycle(item.FullPath);
            vault.Rescan();
            return Json(new { ok = true });
        }));

        app.MapPost("/api/reveal", async (HttpRequest req) => await HandleAsync(async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body);
            var item = vault.Find(body?["id"]?.GetValue<string>() ?? "");
            if (item is null) return Results.NotFound();
            FileOperations.RevealInExplorer(item.FullPath);
            return Json(new { ok = true });
        }));

        // ---------- 设置 ----------
        app.MapPost("/api/roots", async (HttpRequest req) => await HandleAsync(async () =>
        {
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
            var path = body?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path)) return Err("路径为空", 400);
            var source = ParseSource(body?["source"]?.GetValue<string>());
            vault.AddRoot(new LibraryRoot { Path = path, Source = source });
            vault.Rescan();
            return Json(new { ok = true, roots = SerializeRoots(vault) });
        }));

        app.MapDelete("/api/roots", async (HttpRequest req) => await HandleAsync(async () =>
        {
            var path = (await JsonNode.ParseAsync(req.Body))?["path"]?.GetValue<string>();
            vault.RemoveRoot(path ?? "");
            vault.Rescan();
            return Json(new { ok = true, roots = SerializeRoots(vault) });
        }));

        // ---------- 酒馆检测 ----------
        app.MapPost("/api/tavern/detect", () => Handle(() =>
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

        app.MapPost("/api/tavern/connect", async (HttpRequest req) => await HandleAsync(async () =>
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

        app.MapPost("/api/pick-folder", () => Handle(() =>
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

    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or ArgumentException
                                       or InvalidOperationException or Win32Exception)
        {
            return Err(ex.Message, 400);
        }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or ArgumentException
                                       or InvalidOperationException or Win32Exception
                                       or System.Text.Json.JsonException)
        {
            return Err(ex.Message, 400);
        }
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
