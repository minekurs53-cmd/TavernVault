using System.Buffers.Binary;
using System.Text;

namespace TavernVault.Core.Tests;

/// <summary>测试辅助：在内存里构造最小 PNG（IHDR + IEND，可插入任意 tEXt 块）。</summary>
public static class TestPng
{
    public static byte[] Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Build(params (string Type, byte[] Data)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        // IHDR：13 字节数据（1x1 灰度图头）
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // color type
        ms.Write(Chunk("IHDR", ihdr));
        foreach (var (type, data) in chunks)
            ms.Write(Chunk(type, data));
        ms.Write(Chunk("IEND", []));
        return ms.ToArray();
    }

    public static (string Type, byte[] Data) Text(string keyword, string value)
    {
        var key = Encoding.Latin1.GetBytes(keyword);
        var val = Encoding.Latin1.GetBytes(value);
        var data = new byte[key.Length + 1 + val.Length];
        key.CopyTo(data, 0);
        data[key.Length] = 0;
        val.CopyTo(data, key.Length + 1);
        return ("tEXt", data);
    }

    public static byte[] Chunk(string type, byte[] data)
    {
        var raw = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(raw, (uint)data.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(raw, 4);
        data.CopyTo(raw, 8);
        // 注意：数组上 raw[^4..] 会产生拷贝，必须用 AsSpan 原地写入 CRC
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(^4), Crc32(typeu8(raw[4..8]), data));
        return raw;
    }

    private static byte[] typeu8(byte[] typeAndData) => typeAndData;

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
