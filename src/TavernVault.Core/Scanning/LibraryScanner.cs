using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TavernVault.Core.Cards;
using TavernVault.Core.Detection;
using TavernVault.Core.Models;

namespace TavernVault.Core.Scanning;

/// <summary>递归扫描库目录并构建索引。类型识别基于内容。</summary>
public sealed class LibraryScanner
{
    public List<LibraryItem> Scan(IEnumerable<string> roots, IReadOnlyDictionary<string, LibraryItem> existing)
        => Scan(roots.Select(r => new LibraryRoot { Path = r }), existing);

    public List<LibraryItem> Scan(IEnumerable<LibraryRoot> roots, IReadOnlyDictionary<string, LibraryItem> existing)
    {
        var result = new List<LibraryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingByPath = existing.Values
            .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(i => i.FullPath, StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) continue;
            var rootFull = Path.GetFullPath(root.Path);
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };

            foreach (var path in Directory.EnumerateFiles(root.Path, "*", opts))
            {
                var ext = Path.GetExtension(path);
                if (ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                var full = Path.GetFullPath(path);
                if (!seen.Add(full)) continue;
                if (IsInExcludedDir(rootFull, full)) continue;

                var item = BuildItem(full, rootFull, root.Source, existingByPath, existing);
                if (item is not null) result.Add(item);
            }
        }
        return result;
    }

    /// <summary>相对根目录的任意一级目录被排除（.开头 / node_modules）则跳过。</summary>
    private static bool IsInExcludedDir(string rootFull, string fullPath)
    {
        var rel = Path.GetRelativePath(rootFull, fullPath);
        foreach (var seg in rel.Split('/', '\\'))
        {
            if (seg.StartsWith('.') || seg.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>构建单文件条目（Vault.UpsertItem 增量更新复用）。existing 为空时每次都全量解析。</summary>
    public static LibraryItem? BuildItem(string fullPath, string rootFull, LibrarySource source,
        Dictionary<string, LibraryItem> existingByPath, IReadOnlyDictionary<string, LibraryItem> existingById)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists) return null;

        // 未变化：整体复用旧条目（含用户数据与已解析摘要）
        if (existingByPath.TryGetValue(fullPath, out var prev) &&
            prev.SizeBytes == info.Length &&
            prev.ModifiedAt == info.LastWriteTime &&
            string.Equals(prev.RootPath, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            prev.RootSource = source;
            return prev;
        }

        var id = ComputeId(fullPath);
        var item = new LibraryItem
        {
            Id = id,
            FileName = info.Name,
            FullPath = fullPath,
            RootPath = rootFull,
            RootSource = source,
            RelativeDir = Path.GetRelativePath(rootFull, Path.GetDirectoryName(fullPath)!),
            SizeBytes = info.Length,
            ModifiedAt = info.LastWriteTime,
        };
        if (item.RelativeDir == ".") item.RelativeDir = "";

        // 保留用户数据
        if (existingById.TryGetValue(id, out var old))
        {
            item.Favorite = old.Favorite;
            item.UserTags = old.UserTags;
        }

        Classify(item);
        return item;
    }

    private static void Classify(LibraryItem item)
    {
        var path = item.FullPath;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            if (ext == ".png")
            {
                var card = CharacterCardFile.Load(path);
                if (card is not null)
                {
                    item.Kind = ItemKind.Character;
                    item.HasEmbeddedCard = true;
                    FillFromCard(item, card.AsObject());
                    return;
                }
                item.Kind = ItemKind.Other; // 普通图片
                return;
            }

            if (ext == ".json")
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                if (node is JsonObject obj)
                {
                    item.Kind = TypeDetector.DetectJson(obj);
                    if (item.Kind == ItemKind.Character) FillFromCard(item, obj);
                    else if (item.Kind == ItemKind.Lorebook) FillFromLorebook(item, obj);
                    else if (item.Kind == ItemKind.Preset) FillFromPreset(item, obj);
                    else FillFromGenericJson(item, obj);
                    return;
                }
                item.Kind = ItemKind.Text;
                return;
            }

            item.Kind = TypeDetector.DetectByExtension(path, out _);
            if (item.Kind is ItemKind.Script or ItemKind.Text && item.SizeBytes < 2_000_000)
            {
                var head = ReadHead(path, 400);
                item.Description = head;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or OutOfMemoryException
                                      or FormatException)
        {
            item.Kind = ext == ".json" ? ItemKind.Text : ItemKind.Other;
        }
    }

    private static void FillFromCard(LibraryItem item, JsonObject card)
    {
        var data = CharacterCardFile.GetDataNode(card);
        item.Title = AsString(data["name"]);
        item.Creator = AsString(data["creator"]);
        item.Version = AsString(data["character_version"]);
        item.Description = Clean(AsString(data["description"]), 400);
        if (data["tags"] is JsonArray tags)
            item.Tags = tags.OfType<JsonValue>()
                .Select(t => t.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(20).ToList();

        // 内嵌世界书（V2/V3 data.character_book）
        if (data["character_book"] is JsonObject book && book["entries"] is JsonNode)
        {
            item.HasCharacterBook = true;
            item.EntryCount = CharacterBook.CountEntries(book);
        }
    }

    private static void FillFromLorebook(LibraryItem item, JsonObject obj)
    {
        if (obj["entries"] is JsonObject map) item.EntryCount = map.Count;
        else if (obj["entries"] is JsonArray arr) item.EntryCount = arr.Count;

        // 预览：取前几条的备注 / 内容
        var entries = obj["entries"] switch
        {
            JsonObject m => m.Select(p => p.Value).OfType<JsonObject>(),
            JsonArray a => a.OfType<JsonObject>(),
            _ => Enumerable.Empty<JsonObject>(),
        };
        var preview = string.Join("；", entries
            .Take(4)
            .Select(e => AsString(e["comment"]) is { Length: > 0 } c ? c : Clean(AsString(e["content"]), 60))
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        item.Description = Clean(preview, 300);
        item.Title = AsString(obj["name"]);
    }

    private static void FillFromPreset(LibraryItem item, JsonObject obj)
    {
        if (obj["prompts"] is JsonArray arr) item.EntryCount = arr.Count;
        item.Title = AsString(obj["name"]);
        var model = AsString(obj["openai_model"]);
        item.Description = model is { Length: > 0 } ? $"模型：{Clean(model, 80)}" : null;
    }

    private static void FillFromGenericJson(LibraryItem item, JsonObject obj)
    {
        item.Title = Clean(AsString(obj["name"]) ?? "", 80);
        if (obj["content"] is JsonValue v && v.TryGetValue<string>(out var content))
            item.Description = Clean(content, 300);
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? ReadHead(string path, int maxChars)
    {
        using var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buf = new char[maxChars];
        int n = sr.Read(buf, 0, maxChars);
        return Clean(new string(buf, 0, n), maxChars);
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("\r", " ").Replace("\n", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    public static string ComputeId(string fullPath)
    {
        var norm = Path.GetFullPath(fullPath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
