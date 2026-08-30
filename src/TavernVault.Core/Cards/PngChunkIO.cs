using System.Buffers.Binary;
using System.Text;

namespace TavernVault.Core.Cards;

/// <summary>
/// 最低限度的 PNG 分块（chunk）读写器：只解析块结构，可无损替换/插入 tEXt 块，
/// 其余块原样保留（含 CRC）。用于读写角色卡内嵌的 chara / ccv3 数据。
/// </summary>
public static class PngChunkIO
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const uint TextType = 0x74455874; // "tEXt"

    public static bool IsPng(string path)
    {
        using var fs = OpenRead(path);
        if (fs is null) return false;
        Span<byte> head = stackalloc byte[8];
        return fs.Read(head) == 8 && head.SequenceEqual(Signature);
    }

    /// <summary>读取指定关键字（keyword）的 tEXt 块内容；不存在返回 null。</summary>
    public static string? ReadText(string path, string keyword)
    {
        foreach (var raw in EnumerateChunks(path))
        {
            if (TypeOf(raw) != TextType) continue;
            var data = DataOf(raw);
            var sep = data.IndexOf((byte)0);
            if (sep < 0) continue;
            if (Encoding.Latin1.GetString(data[..sep]) != keyword) continue;
            return Encoding.Latin1.GetString(data[(sep + 1)..]);
        }
        return null;
    }

    /// <summary>
    /// 将 tEXt 块（keyword → text）写入 PNG：已有同关键字块则原位替换，否则插到 IHDR 之后。
    /// 未涉及的块（含其 CRC）字节级保留。通过临时文件 + File.Replace 原子落盘。
    /// </summary>
    public static void WriteText(string path, string keyword, string text) =>
        WriteTexts(path, [(keyword, text)]);

    /// <summary>一次遍历写入多个 tEXt 块（如 chara + ccv3），避免整文件重复重写。</summary>
    public static void WriteTexts(string path, IReadOnlyList<(string Keyword, string Text)> items)
    {
        var payloads = items.Select(i => (i.Keyword, Chunk: BuildTextChunk(i.Keyword, i.Text))).ToList();
        var chunks = new List<byte[]>();
        var replaced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in EnumerateChunks(path))
        {
            bool handled = false;
            if (TypeOf(raw) == TextType)
            {
                var data = DataOf(raw);
                var sep = data.IndexOf((byte)0);
                if (sep >= 0)
                {
                    var key = Encoding.Latin1.GetString(data[..sep]);
                    var hit = payloads.FindIndex(p => p.Keyword == key && !replaced.Contains(p.Keyword));
                    if (hit >= 0)
                    {
                        chunks.Add(payloads[hit].Chunk);
                        replaced.Add(payloads[hit].Keyword);
                        handled = true;
                    }
                }
            }
            if (!handled) chunks.Add(raw);
        }

        // 未被替换的（新增关键字）按给定顺序插到 IHDR 之后
        var inserts = payloads.Where(p => !replaced.Contains(p.Keyword)).Select(p => p.Chunk).ToList();
        if (inserts.Count > 0)
        {
            int insertAt = chunks.Count > 0 && TypeOf(chunks[0]) == 0x49484452 ? 1 : 0; // "IHDR"
            chunks.InsertRange(insertAt, inserts);
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);
        foreach (var c in chunks) ms.Write(c);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, ms.ToArray());
        try
        {
            File.Replace(tmp, path, null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tmp, path, overwrite: true);
        }
    }

    // ---- 内部 ----

    private static FileStream? OpenRead(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (IOException) { return null; }
    }

    /// <summary>逐块产出完整的原始块字节（长度 + 类型 + 数据 + CRC）。</summary>
    private static IEnumerable<byte[]> EnumerateChunks(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[8];
        if (fs.Read(head) != 8 || !head.SequenceEqual(Signature)) yield break;

        var lenBuf = new byte[4];
        while (true)
        {
            if (fs.Read(lenBuf) < 4) yield break;
            uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            if (len > int.MaxValue - 12) yield break; // 异常块，防御性终止

            var raw = new byte[12 + (int)len];
            BinaryPrimitives.WriteUInt32BigEndian(raw, len);
            // 一次读入 type + data + crc，流位置才能对齐到下一块
            if (fs.Read(raw, 4, 8 + (int)len) < 8 + (int)len) yield break;
            yield return raw;
        }
    }

    private static uint TypeOf(byte[] raw) => BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4, 4));

    private static byte[] DataOf(byte[] raw) => raw[8..^4];

    private static byte[] BuildTextChunk(string keyword, string text)
    {
        var keyBytes = Encoding.Latin1.GetBytes(keyword);
        var valBytes = Encoding.Latin1.GetBytes(text);
        var data = new byte[keyBytes.Length + 1 + valBytes.Length];
        keyBytes.CopyTo(data, 0);
        data[keyBytes.Length] = 0;
        valBytes.CopyTo(data, keyBytes.Length + 1);

        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        uint crc = Crc32.Compute(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(^4), crc);
        return chunk;
    }
}

file static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint poly = 0xEDB88320;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
