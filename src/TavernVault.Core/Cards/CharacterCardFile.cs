using System.Text;
using System.Text.Json.Nodes;
using TavernVault.Core.Detection;

namespace TavernVault.Core.Cards;

/// <summary>
/// 角色卡的读取与保存。支持三种载体：
/// PNG 内嵌 tEXt 块（chara / ccv3）、V2/V3 JSON（spec + data）、V1 JSON（平铺字段）。
/// </summary>
public static class CharacterCardFile
{
    public const string CharaKey = "chara";
    public const string Ccv3Key = "ccv3";

    /// <summary>尝试把文件加载为角色卡 JSON 树；不是角色卡返回 null。</summary>
    public static JsonNode? Load(string path)
    {
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (!PngChunkIO.IsPng(path)) return null;
            var raw = PngChunkIO.ReadText(path, CharaKey) ?? PngChunkIO.ReadText(path, Ccv3Key);
            if (raw is null) return null;
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim()));
                var node = JsonNode.Parse(json);
                return node is JsonObject ? node : null;
            }
            catch (FormatException) { return null; }
            catch (System.Text.Json.JsonException) { return null; }
        }

        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject obj) return null;
            return LooksLikeCard(obj) ? node : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>取卡片的数据节点：V2/V3 取 data，V1 取根节点。</summary>
    public static JsonObject GetDataNode(JsonObject cardRoot)
    {
        if (cardRoot["data"] is JsonObject data) return data;
        return cardRoot;
    }

    /// <summary>把编辑后的卡片树保存回原文件（PNG 重新内嵌，JSON 直接重写）。</summary>
    public static void Save(string path, JsonObject cardRoot)
    {
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            var data = GetDataNode(cardRoot);
            var v2 = new JsonObject
            {
                ["spec"] = "chara_card_v2",
                ["spec_version"] = "2.0",
                ["data"] = data.DeepClone(),
            };
            var v3 = new JsonObject
            {
                ["spec"] = "chara_card_v3",
                ["spec_version"] = "3.0",
                ["data"] = data.DeepClone(),
            };
            // 一次重写同时更新两个块（大 PNG 避免双倍 IO）
            PngChunkIO.WriteTexts(path,
            [
                (CharaKey, Encode(v2)),
                (Ccv3Key, Encode(v3)),
            ]);
            return;
        }

        SyncLegacyMirror(cardRoot);
        File.WriteAllText(path, cardRoot.ToJsonString(JsonOptions.WriteIndented), new UTF8Encoding(false));
    }

    // ST 的 JSON 导出格式在根级保留了 V1 镜像字段（name/description 等）。
    // 编辑 data 后必须同步这些镜像，否则文件内部自相矛盾。
    private static readonly string[] LegacyMirrorKeys =
    ["name", "description", "personality", "scenario", "first_mes", "mes_example"];

    private static void SyncLegacyMirror(JsonObject cardRoot)
    {
        if (cardRoot["data"] is not JsonObject data) return;
        foreach (var key in LegacyMirrorKeys)
        {
            if (data.ContainsKey(key))
                cardRoot[key] = data[key]!.DeepClone();
            else if (cardRoot.ContainsKey(key))
                cardRoot.Remove(key); // 用户清空了该字段，镜像一并移除
        }
    }

    private static string Encode(JsonObject node) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString(JsonOptions.Compact)));

    private static bool LooksLikeCard(JsonObject obj)
    {
        if (obj["spec"] is JsonValue v && v.TryGetValue<string>(out var spec) &&
            spec.StartsWith("chara_card", StringComparison.OrdinalIgnoreCase))
            return true;
        if (obj["data"] is JsonObject data && data["name"] is not null && data["description"] is not null)
            return true;
        // V1 平铺结构
        return obj["name"] is not null && obj["description"] is not null &&
               (obj["first_mes"] is not null || obj["personality"] is not null || obj["scenario"] is not null);
    }
}

public static class JsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions WriteIndented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly System.Text.Json.JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
