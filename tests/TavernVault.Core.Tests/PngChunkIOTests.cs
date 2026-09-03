using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using TavernVault.Core.Cards;

namespace TavernVault.Core.Tests;

public class PngChunkIOTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tavernvault-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PngChunkIOTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WritePng(byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ReadText_ReturnsValue_ForExistingChunk()
    {
        var path = WritePng(TestPng.Build(TestPng.Text("chara", "aGVsbG8=")));
        Assert.Equal("aGVsbG8=", PngChunkIO.ReadText(path, "chara"));
        Assert.Null(PngChunkIO.ReadText(path, "ccv3"));
    }

    [Fact]
    public void WriteText_InsertsChunk_AfterIHDR()
    {
        var path = WritePng(TestPng.Build());
        PngChunkIO.WriteTexts(path, [("chara", "abc123")]);
        Assert.Equal("abc123", PngChunkIO.ReadText(path, "chara"));
    }

    [Fact]
    public void WriteText_ReplacesExistingChunk_AndPreservesOthers()
    {
        var marker = "important";
        var path = WritePng(TestPng.Build(
            TestPng.Text("chara", "old"),
            ("zTXt", Encoding.Latin1.GetBytes(marker)),
            TestPng.Text("ccv3", "oldv3")));

        PngChunkIO.WriteTexts(path, [("chara", "new-value")]);

        Assert.Equal("new-value", PngChunkIO.ReadText(path, "chara"));
        Assert.Equal("oldv3", PngChunkIO.ReadText(path, "ccv3"));
        // 其余块字节未动
        using var fs = File.OpenRead(path);
        var text = Encoding.Latin1.GetString(ReadAll(fs));
        Assert.Contains("zTXt" + (char)0 + marker, text);
    }

    [Fact]
    public void WriteText_WritesValidCrc()
    {
        var path = WritePng(TestPng.Build());
        PngChunkIO.WriteTexts(path, [("chara", "payload")]);

        var bytes = File.ReadAllBytes(path);
        // 找到 tEXt 块并校验 CRC
        int pos = 8;
        while (pos < bytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
            var type = Encoding.Latin1.GetString(bytes, pos + 4, 4);
            if (type == "tEXt")
            {
                var expected = Crc(bytes, pos + 4, 4 + (int)len);
                var actual = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 8 + (int)len));
                Assert.Equal(expected, actual);
                return;
            }
            pos += 12 + (int)len;
        }
        Assert.Fail("未找到 tEXt 块");
    }

    [Fact]
    public void IsPng_DetectsSignature()
    {
        var png = WritePng(TestPng.Build());
        var bin = Path.Combine(_dir, "not.png");
        File.WriteAllBytes(bin, [1, 2, 3, 4]);
        Assert.True(PngChunkIO.IsPng(png));
        Assert.False(PngChunkIO.IsPng(bin));
    }

    [Fact]
    public void CharacterCardFile_Save_Png_PreservesImageChunks()
    {
        // 回归：v0.5.0 修复的另存为数据损坏 bug——Save 只重嵌 tEXt，图像块必须字节级保留
        var idat = new byte[] { 0x00, 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33 };
        var card = new JsonObject
        {
            ["spec"] = "chara_card_v2",
            ["spec_version"] = "2.0",
            ["data"] = new JsonObject { ["name"] = "原卡", ["description"] = "描述" },
        };
        var path = WritePng(TestPng.Build(
            ("IDAT", idat),
            TestPng.Text("chara", Convert.ToBase64String(Encoding.UTF8.GetBytes(card.ToJsonString())))));

        var loaded = CharacterCardFile.Load(path) as JsonObject;
        Assert.NotNull(loaded);
        CharacterCardFile.GetDataNode(loaded!)["name"] = "编辑后";
        CharacterCardFile.Save(path, loaded!);

        var chunks = ReadChunks(File.ReadAllBytes(path));
        Assert.Contains("IHDR", chunks.Keys);
        Assert.Contains("IEND", chunks.Keys);
        Assert.Equal(idat, chunks.GetValueOrDefault("IDAT"));
        var reloaded = CharacterCardFile.Load(path) as JsonObject;
        Assert.Equal("编辑后", CharacterCardFile.GetDataNode(reloaded!)["name"]!.GetValue<string>());
    }

    private static Dictionary<string, byte[]> ReadChunks(byte[] bytes)
    {
        var result = new Dictionary<string, byte[]>();
        int pos = 8; // 跳过签名
        while (pos + 12 <= bytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
            var type = Encoding.Latin1.GetString(bytes, pos + 4, 4);
            result[type] = bytes[(pos + 8)..(pos + 8 + (int)len)];
            pos += 12 + (int)len;
        }
        return result;
    }

    private static byte[] ReadAll(FileStream fs)
    {
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static uint Crc(byte[] data, int start, int count)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = start; i < start + count; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
