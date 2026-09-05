using System.Text.Json.Nodes;

namespace TavernVault.Core.Cards;

/// <summary>
/// 角色卡内嵌世界书（data.character_book）的读取与写回。
/// 社区卡片存在两种条目格式：
///   - Spec V2 标准：keys / secondary_keys / enabled / insertion_order / position("before_char"|"after_char")
///   - ST 内部格式：key / keysecondary / disable / order / position(0-6 整数)
/// 读取时统一转换为 ST 格式供编辑器使用；写回时逐条保留原始格式，
/// Spec 条目只覆盖被编辑的字段，id / selective / use_regex / extensions 等原样保留。
/// </summary>
public static class CharacterBook
{
    public sealed class BookEntry
    {
        /// <summary>entries 容器中的键（数组容器时为索引字符串）。</summary>
        public string MapKey { get; set; } = "";
        /// <summary>ST 格式条目（编辑器所见所改）。</summary>
        public JsonObject St { get; set; } = new();
        /// <summary>非 null 表示原始条目是 Spec 格式，写回时把编辑合并进它的克隆。</summary>
        public JsonObject? Raw { get; set; }
    }

    public static JsonObject? FindBook(JsonObject cardData) =>
        cardData["character_book"] as JsonObject;

    public static bool HasBook(JsonObject cardData) =>
        FindBook(cardData)?["entries"] is JsonNode;

    public static int CountEntries(JsonObject book) => book["entries"] switch
    {
        JsonObject map => map.Count,
        JsonArray arr => arr.Count,
        _ => 0,
    };

    /// <summary>读取全部条目并规范为 ST 格式。</summary>
    public static List<BookEntry> ReadEntries(JsonObject book)
    {
        var result = new List<BookEntry>();
        switch (book["entries"])
        {
            case JsonObject map:
                foreach (var (k, v) in map)
                    if (v is JsonObject o)
                        result.Add(FromNode(k, o));
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                    if (arr[i] is JsonObject o)
                        result.Add(FromNode(i.ToString(), o));
                break;
        }
        return result;
    }

    /// <summary>
    /// 把编辑后的条目写回书节点。容器形态（对象/数组）沿用书节点当前的形态，
    /// 每条按其来源格式写回：Spec 条目合并编辑、ST 条目原样替换。
    /// v0.6.0：St 与 Spec→ST 转换结果一致的条目视为未编辑，直接写回 Raw 原文
    /// （字节级保形，合并不再给 extensions 等补默认值）。
    /// </summary>
    public static void WriteEntries(JsonObject book, IReadOnlyList<BookEntry> entries)
    {
        bool asMap = book["entries"] is not JsonArray; // 缺省（新建书）也用对象容器
        var map = new JsonObject();
        var list = new JsonArray();

        foreach (var e in entries)
        {
            JsonNode written;
            if (e.Raw is null)
            {
                written = e.St.DeepClone();
            }
            else
            {
                var raw = e.Raw.DeepClone().AsObject();
                written = JsonNode.DeepEquals(SpecToSt(raw), e.St) ? raw : MergeIntoSpec(raw, e.St);
            }
            if (asMap) map[e.MapKey] = written;
            else list.Add(written);
        }
        book["entries"] = asMap ? map : list;
    }

    /// <summary>为没有内置书的卡片创建空书（Spec V2 形态，空数组条目）。</summary>
    public static JsonObject CreateBook() => new()
    {
        ["name"] = "",
        ["entries"] = new JsonArray(),
    };


    // ---- 内部 ----

    private static BookEntry FromNode(string mapKey, JsonObject node)
    {
        if (IsSpecEntry(node))
        {
            return new BookEntry { MapKey = mapKey, St = SpecToSt(node), Raw = node };
        }
        // ST 格式：原样交给编辑器
        return new BookEntry { MapKey = mapKey, St = node.DeepClone().AsObject(), Raw = null };
    }

    private static bool IsSpecEntry(JsonObject o) =>
        o.ContainsKey("keys") || o.ContainsKey("enabled") || o.ContainsKey("insertion_order");

    private static JsonObject SpecToSt(JsonObject spec)
    {
        JsonObject? ext = spec["extensions"] as JsonObject;
        var st = new JsonObject
        {
            ["key"] = spec["keys"]?.DeepClone() ?? new JsonArray(),
            ["keysecondary"] = spec["secondary_keys"]?.DeepClone() ?? new JsonArray(),
            ["content"] = spec["content"]?.DeepClone() ?? "",
            ["comment"] = spec["comment"]?.DeepClone() ?? "",
            ["constant"] = spec["constant"]?.GetValue<bool>() ?? false,
            ["disable"] = !(spec["enabled"]?.GetValue<bool>() ?? true),
            ["order"] = spec["insertion_order"]?.GetValue<int>() ?? 100,
            ["position"] = PositionSpecToSt(spec["position"]),
            ["depth"] = ext?["depth"]?.GetValue<int>() ?? 4,
            ["probability"] = ext?["use_probability"]?.GetValue<int>() ?? 100,
            ["caseSensitive"] = spec["case_sensitive"]?.GetValue<bool>() ?? false,
        };
        return st;
    }

    private static int PositionSpecToSt(JsonNode? p)
    {
        if (p is not JsonValue v) return 0;
        if (v.TryGetValue<string>(out var s)) return s == "after_char" ? 1 : 0;
        if (v.TryGetValue<int>(out var n)) return n;
        return 0;
    }

    private static int PositionSt(JsonNode? p) =>
        p is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>把 ST 格式的编辑合并进 Spec 原条目克隆；未涉及的字段全部保留。</summary>
    private static JsonObject MergeIntoSpec(JsonObject raw, JsonObject st)
    {
        raw["keys"] = st["key"]?.DeepClone() ?? new JsonArray();
        raw["secondary_keys"] = st["keysecondary"]?.DeepClone() ?? new JsonArray();
        raw["content"] = st["content"]?.DeepClone() ?? "";
        raw["comment"] = st["comment"]?.DeepClone() ?? "";
        raw["constant"] = st["constant"]?.GetValue<bool>() ?? false;
        raw["enabled"] = !(st["disable"]?.GetValue<bool>() ?? false);
        raw["insertion_order"] = st["order"]?.GetValue<int>() ?? 100;

        int stPos = PositionSt(st["position"]);
        if (raw["position"] is JsonValue pv && pv.TryGetValue<string>(out _))
            raw["position"] = stPos == 1 ? "after_char" : "before_char";
        else
            raw["position"] = stPos;

        if (raw["extensions"] is not JsonObject ext)
        {
            ext = new JsonObject();
            raw["extensions"] = ext;
        }
        ext["depth"] = st["depth"]?.GetValue<int>() ?? 4;
        ext["use_probability"] = st["probability"]?.GetValue<int>() ?? 100;

        if (raw.ContainsKey("case_sensitive"))
            raw["case_sensitive"] = st["caseSensitive"]?.GetValue<bool>() ?? false;

        return raw;
    }
}
