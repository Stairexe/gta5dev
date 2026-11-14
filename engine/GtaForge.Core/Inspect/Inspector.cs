using System.Globalization;
using System.Xml;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using GtaForge.Core.Gta;

namespace GtaForge.Core.Inspect;

/// <summary>Turns a compiled .ydr/.ydd/.yft (+ optional .ytd) into JSON meshes for the browser viewer.</summary>
public static class Inspector
{
    public sealed record PreviewMesh(string Drawable, int Shader, string? Texture, float[] Positions, float[] Normals, float[] Uv, int[] Indices);
    public sealed record PreviewBone(string Name, int Parent, float[] T, float[] R);
    public sealed record Preview(string File, string Kind, List<string> Drawables, List<PreviewMesh> Meshes, List<PreviewBone> Bones,
                                 Dictionary<string, string> Textures, int Triangles);

    const int MaxPreviewTexture = 512;

    public static Preview Inspect(string fileName, byte[] data, byte[]? ytd = null)
    {
        var lower = fileName.ToLowerInvariant();
        var work = Directory.CreateTempSubdirectory("gf_insp_").FullName;
        try
        {
            string xml; string kind; string root;
            var textures = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
            if (lower.EndsWith(".ydd"))
            {
                var f = GtaIO.LoadYdd(data, fileName);
                xml = YddXml.GetXml(f, work); kind = "ydd"; root = "DrawableDictionary/Item";
                foreach (var d in f.Drawables ?? []) AddTextures(textures, d.ShaderGroup?.TextureDictionary);
            }
            else if (lower.EndsWith(".yft"))
            {
                var f = GtaIO.LoadYft(data, fileName);
                xml = YftXml.GetXml(f, work); kind = "yft"; root = "Fragment/Drawable";
                AddTextures(textures, f.Fragment?.Drawable?.ShaderGroup?.TextureDictionary);
            }
            else if (lower.EndsWith(".ydr"))
            {
                var f = GtaIO.LoadYdr(data, fileName);
                xml = YdrXml.GetXml(f, work); kind = "ydr"; root = "Drawable";
                AddTextures(textures, f.Drawable?.ShaderGroup?.TextureDictionary);
            }
            else throw new ArgumentException("Preview supports .ydr, .ydd and .yft files.");

            if (ytd != null) AddTextures(textures, GtaIO.LoadYtd(ytd).TextureDict);

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var drawables = doc.SelectNodes(root)!.OfType<XmlElement>().ToList();
            var meshes = new List<PreviewMesh>();
            var names = new List<string>();
            var usedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PreviewBone>? bones = null;
            foreach (var d in drawables)
            {
                var dname = d.SelectSingleNode("Name")?.InnerText ?? kind;
                names.Add(dname);
                bones ??= ReadBones(d);
                var shaderTex = d.SelectNodes("ShaderGroup/Shaders/Item")!.OfType<XmlElement>()
                    .Select(s => s.SelectSingleNode("Parameters/Item[@name='DiffuseSampler']/Name")?.InnerText).ToList();
                foreach (XmlElement g in d.SelectNodes("DrawableModelsHigh/Item/Geometries/Item")!)
                {
                    var m = ReadGeometry(dname, g, shaderTex);
                    if (m == null) continue;
                    meshes.Add(m);
                    if (m.Texture != null) usedTextures.Add(m.Texture);
                }
            }

            var texOut = new Dictionary<string, string>();
            foreach (var t in usedTextures)
                if (textures.TryGetValue(t, out var tex) && ToPngDataUrl(tex) is { } url) texOut[t] = url;

            return new Preview(fileName, kind, names, meshes, bones ?? [], texOut, meshes.Sum(m => m.Indices.Length / 3));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp cleanup */ }
        }
    }

    static void AddTextures(Dictionary<string, Texture> into, TextureDictionary? td)
    {
        foreach (var t in td?.Textures?.data_items ?? []) into[t.Name] = t;
    }

    static string? ToPngDataUrl(Texture tex)
    {
        try
        {
            int mip = 0, w = tex.Width, h = tex.Height;
            while (Math.Max(w, h) > MaxPreviewTexture && mip < tex.Levels - 1) { mip++; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
            var px = DDSIO.GetPixels(tex, mip);
            // CodeWalker returns BGRA; swap to RGBA.
            for (int i = 0; i + 3 < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return "data:image/png;base64," + Convert.ToBase64String(Png.Encode(px, w, h));
        }
        catch { return null; }
    }

    static List<PreviewBone> ReadBones(XmlElement d)
    {
        var list = new List<PreviewBone>();
        foreach (XmlElement b in d.SelectNodes("Skeleton/Bones/Item")!)
        {
            float A(string tag, string a) => float.Parse(((XmlElement)b.SelectSingleNode(tag)!).GetAttribute(a), CultureInfo.InvariantCulture);
            list.Add(new PreviewBone(
                b.SelectSingleNode("Name")?.InnerText ?? "",
                int.Parse(((XmlElement)b.SelectSingleNode("ParentIndex")!).GetAttribute("value"), CultureInfo.InvariantCulture),
                [A("Translation", "x"), A("Translation", "y"), A("Translation", "z")],
                [A("Rotation", "x"), A("Rotation", "y"), A("Rotation", "z"), A("Rotation", "w")]));
        }
        return list;
    }

    static readonly Dictionary<string, int> Width = new()
    {
        ["Position"] = 3, ["BlendWeights"] = 4, ["BlendIndices"] = 4, ["Normal"] = 3, ["Colour0"] = 4, ["Colour1"] = 4,
        ["Tangent"] = 4, ["Binormal"] = 4, ["TexCoord0"] = 2, ["TexCoord1"] = 2, ["TexCoord2"] = 2, ["TexCoord3"] = 2,
        ["TexCoord4"] = 2, ["TexCoord5"] = 2, ["TexCoord6"] = 2, ["TexCoord7"] = 2,
    };

    static PreviewMesh? ReadGeometry(string drawable, XmlElement g, List<string?> shaderTex)
    {
        var layoutNode = g.SelectSingleNode("VertexBuffer/Layout");
        var dataNode = g.SelectSingleNode("VertexBuffer/Data");
        var idxNode = g.SelectSingleNode("IndexBuffer/Data");
        if (layoutNode == null || dataNode == null || idxNode == null) return null;
        var layout = layoutNode.ChildNodes.OfType<XmlElement>().Select(e => e.Name).ToArray();
        int Off(string n) { int o = 0; foreach (var l in layout) { if (l == n) return o; o += Width.GetValueOrDefault(l, 4); } return -1; }
        int p = Off("Position"), n = Off("Normal"), t = Off("TexCoord0");
        var rows = dataNode.InnerText.Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToArray();
        var pos = new float[rows.Length * 3]; var nrm = new float[rows.Length * 3]; var uv = new float[rows.Length * 2];
        for (int v = 0; v < rows.Length; v++)
        {
            var c = rows[v].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            float F(int i) => float.Parse(c[i], CultureInfo.InvariantCulture);
            pos[v * 3] = F(p); pos[v * 3 + 1] = F(p + 1); pos[v * 3 + 2] = F(p + 2);
            if (n >= 0) { nrm[v * 3] = F(n); nrm[v * 3 + 1] = F(n + 1); nrm[v * 3 + 2] = F(n + 2); }
            if (t >= 0) { uv[v * 2] = F(t); uv[v * 2 + 1] = F(t + 1); }
        }
        var idx = idxNode.InnerText.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        int shader = int.Parse(((XmlElement)g.SelectSingleNode("ShaderIndex")!).GetAttribute("value"), CultureInfo.InvariantCulture);
        return new PreviewMesh(drawable, shader, shader < shaderTex.Count ? shaderTex[shader] : null, pos, nrm, uv, idx);
    }
}
