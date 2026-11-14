using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using StbImageSharp;

namespace GtaForge.Core.Gta;

public enum TextureKind { Diffuse, Normal, Specular }

public sealed record TextureInput(string Name, byte[] ImageBytes, TextureKind Kind);

/// <summary>Turns PNG/JPG/TGA images into DXT-compressed DDS files and a .ytd texture dictionary.</summary>
public static class TextureWriter
{
    public const int MaxSize = 2048;

    public sealed record Encoded(string Name, string DdsFile, int Width, int Height, string Format);

    public static byte[] BuildYtd(IEnumerable<TextureInput> textures, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var encoded = textures.Select(t => Encode(t, workDir)).ToList();
        var x = new Xw();
        x.Open("TextureDictionary");
        foreach (var e in encoded)
        {
            x.Open("Item");
            x.Text("Name", e.Name);
            x.Value("Unk32", 0);
            x.Text("Usage", "UNKNOWN");
            x.Text("UsageFlags", "0");
            x.Value("ExtraFlags", 0);
            x.Value("Width", e.Width);
            x.Value("Height", e.Height);
            x.Value("MipLevels", 1);
            x.Text("Format", e.Format);
            x.Text("FileName", e.DdsFile);
            x.Close("Item");
        }
        x.Close("TextureDictionary");
        return GtaIO.YtdFromXml(x.ToString(), workDir);
    }

    public static Encoded Encode(TextureInput t, string workDir)
    {
        var img = ImageResult.FromMemory(t.ImageBytes, ColorComponents.RedGreenBlueAlpha);
        var (w, h) = (Pow2(img.Width), Pow2(img.Height));
        var rgba = (w == img.Width && h == img.Height) ? img.Data : Resize(img.Data, img.Width, img.Height, w, h);

        bool hasAlpha = false;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 250) { hasAlpha = true; break; }
        var bc1 = t.Kind == TextureKind.Diffuse && !hasAlpha;

        var enc = new BcEncoder();
        enc.OutputOptions.GenerateMipMaps = true;
        enc.OutputOptions.Quality = CompressionQuality.Balanced;
        enc.OutputOptions.Format = bc1 ? CompressionFormat.Bc1 : CompressionFormat.Bc3;
        enc.OutputOptions.FileFormat = OutputFileFormat.Dds;
        enc.OutputOptions.DdsPreferDxt10Header = false;

        var file = SafeName(t.Name) + ".dds";
        using (var fs = File.Create(Path.Combine(workDir, file)))
        {
            var dds = enc.EncodeToDds(rgba, w, h, PixelFormat.Rgba32);
            dds.Write(fs);
        }
        return new Encoded(t.Name, file, w, h, bc1 ? "D3DFMT_DXT1" : "D3DFMT_DXT5");
    }

    /// <summary>A 4x4 solid colour PNG-equivalent texture for materials that have only a base colour.</summary>
    public static byte[] SolidRgbaPng(byte r, byte g, byte b, byte a = 255)
    {
        // Minimal uncompressed PNG writer to stay dependency-free.
        const int s = 4;
        var raw = new byte[s * (1 + s * 4)];
        for (int y = 0; y < s; y++)
        {
            raw[y * (1 + s * 4)] = 0;
            for (int x = 0; x < s; x++)
            {
                int o = y * (1 + s * 4) + 1 + x * 4;
                raw[o] = r; raw[o + 1] = g; raw[o + 2] = b; raw[o + 3] = a;
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, s); WriteBE(ihdr, 4, s);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true))
                zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();

        static void WriteBE(byte[] buf, int o, int v)
        {
            buf[o] = (byte)(v >> 24); buf[o + 1] = (byte)(v >> 16); buf[o + 2] = (byte)(v >> 8); buf[o + 3] = (byte)v;
        }
        static void Chunk(Stream st, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length);
            st.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            st.Write(td);
            var crc = new byte[4]; WriteBE(crc, 0, (int)System.IO.Hashing.Crc32.HashToUInt32(td));
            st.Write(crc);
        }
    }

    static int Pow2(int v)
    {
        int p = 4;
        while (p < v && p < MaxSize) p <<= 1;
        // pick the nearer power of two (round down if much closer)
        if (p > v && p / 2 >= 4 && v - p / 2 < p - v) p /= 2;
        return Math.Clamp(p, 4, MaxSize);
    }

    static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            float fy = (y + 0.5f) * sh / dh - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, sh - 1), y1 = Math.Min(y0 + 1, sh - 1);
            float ty = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < dw; x++)
            {
                float fx = (x + 0.5f) * sw / dw - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, sw - 1), x1 = Math.Min(x0 + 1, sw - 1);
                float tx = Math.Clamp(fx - x0, 0, 1);
                for (int c = 0; c < 4; c++)
                {
                    float a = src[(y0 * sw + x0) * 4 + c] * (1 - tx) + src[(y0 * sw + x1) * 4 + c] * tx;
                    float b = src[(y1 * sw + x0) * 4 + c] * (1 - tx) + src[(y1 * sw + x1) * 4 + c] * tx;
                    dst[(y * dw + x) * 4 + c] = (byte)Math.Clamp(a * (1 - ty) + b * ty + 0.5f, 0, 255);
                }
            }
        }
        return dst;
    }

    public static string SafeName(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var r = new string(chars).Trim('_');
        return r.Length == 0 ? "tex" : r;
    }
}
