using System.Text.Json.Nodes;
using TavernVault.Core.Cards;
using TavernVault.Core.Models;
using Xunit;

namespace TavernVault.Core.Tests;

/// <summary>
/// 内嵌世界书合入（v0.7.6）：AppendBook 的追加/键分配/格式规范化/已有条目保形。
/// </summary>
public class CharacterBookMergeTests
{
    private static JsonObject CardBook() => new()
    {
        ["name"] = "内嵌书",
        ["entries"] = new JsonObject
        {
            ["0"] = new JsonObject
            {
                ["key"] = new JsonArray("已有词"),
                ["content"] = "已有内容",
                ["comment"] = "已有条目",
                ["disable"] = false,
                ["order"] = 1,
                ["position"] = 0,
            },
        },
    };

    [Fact]
    public void Append_StSource_KeysContinueAfterMax()
    {
        var card = CardBook();
        var source = new JsonObject
        {
            ["entries"] = new JsonObject
            {
                ["0"] = new JsonObject { ["key"] = new JsonArray("词一"), ["content"] = "一", ["disable"] = false, ["order"] = 1, ["position"] = 0 },
                ["1"] = new JsonObject { ["key"] = new JsonArray("词二"), ["content"] = "二", ["disable"] = true, ["order"] = 2, ["position"] = 0 },
            },
        };

        var added = CharacterBook.AppendBook(card, source);

        Assert.Equal(2, added);
        var entries = (JsonObject)card["entries"]!;
        Assert.Equal(3, entries.Count);
        // 追加键从现有最大数字键 +1 起算（来源键 "0/1" 不与已有 "0" 冲突）
        Assert.NotNull(entries["1"]);
        Assert.NotNull(entries["2"]);
        Assert.Equal("一", (string)entries["1"]!["content"]!);
        Assert.Equal("已有内容", (string)entries["0"]!["content"]!); // 已有条目原样
    }

    [Fact]
    public void Append_SpecSource_NormalizedToStFormat()
    {
        var card = CardBook();
        var source = new JsonObject
        {
            ["entries"] = new JsonArray(
                new JsonObject
                {
                    ["keys"] = new JsonArray("Spec词"),
                    ["content"] = "Spec内容",
                    ["enabled"] = false,
                    ["insertion_order"] = 10,
                    ["id"] = 7,
                    ["extensions"] = new JsonObject { ["depth"] = 4 },
                }),
        };

        CharacterBook.AppendBook(card, source);

        var entries = (JsonObject)card["entries"]!;
        var appended = entries["1"]!.AsObject();
        // Spec → ST 规范化：enabled=false → disable=true，keys→key，insertion_order→order
        Assert.True((bool)appended["disable"]!);
        Assert.Equal("Spec内容", (string)appended["content"]!);
        Assert.Equal(10, (int)appended["order"]!);
        Assert.Null(appended["enabled"]); // 不残留 Spec 字段名
        Assert.Null(appended["id"]); // 丢弃来源原文（Raw），容器内保持单一 ST 格式
    }

    [Fact]
    public void Append_ArrayContainerCardBook_StaysArray()
    {
        var card = CharacterBook.CreateBook(); // 内嵌书缺省为数组容器
        var source = CardBook();

        CharacterBook.AppendBook(card, source);

        Assert.IsType<JsonArray>(card["entries"]);
        Assert.Equal(1, card["entries"]!.AsArray().Count);
        Assert.Equal("已有内容", (string)card["entries"]![0]!["content"]!);
    }

    [Fact]
    public void Append_EmptySource_ReturnsZero_NoChange()
    {
        var card = CardBook();
        var before = card.ToJsonString();
        var source = new JsonObject { ["entries"] = new JsonObject() };

        Assert.Equal(0, CharacterBook.AppendBook(card, source));
        Assert.Equal(before, card.ToJsonString());
    }
}
