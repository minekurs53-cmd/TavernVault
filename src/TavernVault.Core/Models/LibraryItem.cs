using System.Text.Json.Serialization;
using TavernVault.Core.Detection;

namespace TavernVault.Core.Models;

/// <summary>库中被索引的一个文件。Id 是完整路径的哈希，移动/重命名后会变化。</summary>
public class LibraryItem
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string RelativeDir { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }

    /// <summary>枚举值（索引持久化用）。</summary>
    [JsonPropertyName("kindValue")]
    public ItemKind Kind { get; set; }

    /// <summary>API 输出用的小写类型键（character / lorebook / ...）。</summary>
    [JsonPropertyName("kind")]
    public string KindKey => ItemKindText.KeyOf(Kind);

    // ---- 来自文件内容的摘要 ----
    public string? Title { get; set; }
    public string? Creator { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
    public int EntryCount { get; set; }
    public bool HasEmbeddedCard { get; set; }

    /// <summary>角色卡 data.character_book 内嵌世界书（EntryCount 为其条目数）。</summary>
    public bool HasCharacterBook { get; set; }

    // ---- 用户数据（重扫描时保留）----
    public bool Favorite { get; set; }
    public List<string> UserTags { get; set; } = [];

    /// <summary>不带扩展名的展示名。</summary>
    [JsonIgnore]
    public string DisplayName => Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(FileName);
}
