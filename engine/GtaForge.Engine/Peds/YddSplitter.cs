using System.Globalization;
using System.Numerics;
using System.Xml;

namespace GtaForge.Engine.Peds;

/// <summary>
/// Splits the drawables of a full-body ped .ydd (CodeWalker XML) into head / uppr / lowr / feet
/// drawables by the bone each triangle is skinned to. Works on the XML text so the original
/// vertex data is copied byte-for-byte.
/// </summary>
public static class YddSplitter
{
    public static readonly string[] LodTags = ["DrawableModelsHigh", "DrawableModelsMedium", "DrawableModelsLow", "DrawableModelsVeryLow"];

    static readonly Dictionary<string, int> ComponentWidth = new()
    {
        ["Position"] = 3, ["BlendWeights"] = 4, ["BlendIndices"] = 4, ["Normal"] = 3,
        ["Colour0"] = 4, ["Colour1"] = 4, ["Tangent"] = 4, ["Binormal"] = 4,
        ["TexCoord0"] = 2, ["TexCoord1"] = 2, ["TexCoord2"] = 2, ["TexCoord3"] = 2,
        ["TexCoord4"] = 2, ["TexCoord5"] = 2, ["TexCoord6"] = 2, ["TexCoord7"] = 2,
    };

    public sealed class Piece
    {
        public required string Component { get; init; }
        /// <summary>LOD tag -> geometry XML elements (cloned, re-indexed).</summary>
        public Dictionary<string, List<XmlElement>> Geometries { get; } = [];
        public int Triangles { get; set; }
        /// <summary>Shader index -> triangle count (to find the main texture of the piece).</summary>
        public Dictionary<int, int> ShaderUse { get; } = [];
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
    }

    /// <summary>Splits every geometry of every LOD of <paramref name="drawable"/> into per-component pieces.</summary>
    public static Dictionary<string, Piece> Split(XmlElement drawable, IReadOnlyList<string> skeletonBoneNames, XmlDocument doc)
    {
        var pieces = new Dictionary<string, Piece>();
        var boneComp = skeletonBoneNames.Select(PedComponents.ForBone).ToArray();

        foreach (var lodTag in LodTags)
        {
            var lod = drawable.SelectSingleNode(lodTag);
            if (lod == null) continue;
            foreach (XmlElement model in lod.SelectNodes("Item")!)
            {
                foreach (XmlElement geom in model.SelectNodes("Geometries/Item")!)
                {
                    foreach (var (comp, newGeom, tris, box) in SplitGeometry(geom, boneComp, doc))
                    {
                        if (!pieces.TryGetValue(comp, out var p)) pieces[comp] = p = new Piece { Component = comp };
                        if (!p.Geometries.TryGetValue(lodTag, out var list)) p.Geometries[lodTag] = list = [];
                        list.Add(newGeom);
                        if (lodTag == "DrawableModelsHigh")
                        {
                            p.Triangles += tris;
                            int shader = int.Parse(geom.SelectSingleNode("ShaderIndex")!.Attributes!["value"]!.Value, CultureInfo.InvariantCulture);
                            p.ShaderUse[shader] = p.ShaderUse.GetValueOrDefault(shader) + tris;
                        }
                        p.Min = Vector3.Min(p.Min, box.min);
                        p.Max = Vector3.Max(p.Max, box.max);
                    }
                }
            }
        }
        return pieces;
    }

    static IEnumerable<(string comp, XmlElement geom, int tris, (Vector3 min, Vector3 max) box)> SplitGeometry(
        XmlElement geom, string[] boneComp, XmlDocument doc)
    {
        var layout = geom.SelectSingleNode("VertexBuffer/Layout")!.ChildNodes.OfType<XmlElement>().Select(e => e.Name).ToArray();
        var rows = geom.SelectSingleNode("VertexBuffer/Data")!.InnerText
            .Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToArray();
        var indices = geom.SelectSingleNode("IndexBuffer/Data")!.InnerText
            .Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var boneIdsText = geom.SelectSingleNode("BoneIDs")?.InnerText;
        int[]? palette = string.IsNullOrWhiteSpace(boneIdsText) ? null
            : boneIdsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();

        // Column offsets of the components we need.
        int Offset(string name)
        {
            int o = 0;
            foreach (var l in layout)
            {
                if (l == name) return o;
                o += ComponentWidth.GetValueOrDefault(l, 4);
            }
            return -1;
        }
        int pos = Offset("Position"), bw = Offset("BlendWeights"), bi = Offset("BlendIndices");
        var cells = rows.Select(r => r.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToArray();

        // Per-vertex dominant component.
        var vComp = new string[rows.Length];
        var vPos = new Vector3[rows.Length];
        for (int v = 0; v < rows.Length; v++)
        {
            var c = cells[v];
            vPos[v] = new Vector3(F(c[pos]), F(c[pos + 1]), F(c[pos + 2]));
            if (bw < 0 || bi < 0) { vComp[v] = "uppr"; continue; }
            var score = new Dictionary<string, int>();
            for (int k = 0; k < 4; k++)
            {
                int w = int.Parse(c[bw + k], CultureInfo.InvariantCulture);
                if (w == 0) continue;
                int idx = int.Parse(c[bi + k], CultureInfo.InvariantCulture);
                int bone = palette != null && idx < palette.Length ? palette[idx] : idx;
                var comp = bone >= 0 && bone < boneComp.Length ? boneComp[bone] : "uppr";
                score[comp] = score.GetValueOrDefault(comp) + w;
            }
            vComp[v] = score.Count == 0 ? "uppr" : score.MaxBy(kv => kv.Value).Key;
        }

        // Triangles go to the majority component of their three vertices.
        var byComp = new Dictionary<string, List<int>>();
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            string a = vComp[indices[t]], b = vComp[indices[t + 1]], c = vComp[indices[t + 2]];
            var comp = a == b || a == c ? a : b == c ? b : a;
            if (!byComp.TryGetValue(comp, out var list)) byComp[comp] = list = [];
            list.Add(indices[t]); list.Add(indices[t + 1]); list.Add(indices[t + 2]);
        }

        foreach (var (comp, idx) in byComp)
        {
            var map = new Dictionary<int, int>();
            var newIdx = new int[idx.Count];
            for (int i = 0; i < idx.Count; i++)
            {
                if (!map.TryGetValue(idx[i], out int n)) { n = map.Count; map[idx[i]] = n; }
                newIdx[i] = n;
            }
            var order = new int[map.Count];
            foreach (var (src, dst) in map) order[dst] = src;

            var clone = (XmlElement)geom.CloneNode(true);
            clone.SelectSingleNode("VertexBuffer/Data")!.InnerText = "\n" + string.Join("\n", order.Select(o => rows[o])) + "\n";
            clone.SelectSingleNode("IndexBuffer/Data")!.InnerText = "\n" + string.Join("\n", newIdx.Chunk(24).Select(ch => string.Join(' ', ch))) + "\n";
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var o in order) { min = Vector3.Min(min, vPos[o]); max = Vector3.Max(max, vPos[o]); }
            SetVec(clone, "BoundingBoxMin", min);
            SetVec(clone, "BoundingBoxMax", max);
            yield return (comp, clone, idx.Count / 3, (min, max));
        }
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    static void SetVec(XmlElement e, string tag, Vector3 v)
    {
        if (e.SelectSingleNode(tag) is not XmlElement n) return;
        n.SetAttribute("x", v.X.ToString(CultureInfo.InvariantCulture));
        n.SetAttribute("y", v.Y.ToString(CultureInfo.InvariantCulture));
        n.SetAttribute("z", v.Z.ToString(CultureInfo.InvariantCulture));
        if (n.HasAttribute("w")) n.SetAttribute("w", "0");
    }
}
