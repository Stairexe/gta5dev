using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace GtaForge.Core.Gta;

/// <summary>Minimal RGBA8 PNG encoder (no dependencies).</summary>
public static class Png
{
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BE(ihdr, 0, width); BE(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void BE(byte[] buf, int o, int v)
    {
        buf[o] = (byte)(v >> 24); buf[o + 1] = (byte)(v >> 16); buf[o + 2] = (byte)(v >> 8); buf[o + 3] = (byte)v;
    }

    static void Chunk(Stream st, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length);
        st.Write(len);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        st.Write(td);
        var crc = new byte[4]; BE(crc, 0, (int)Crc32.HashToUInt32(td));
        st.Write(crc);
    }
}
