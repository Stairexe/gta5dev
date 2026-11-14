using System.Globalization;
using System.Numerics;

namespace GtaForge.Core.Gta;

/// <summary>Writes a CodeWalker drawable XML (.ydr.xml) from the neutral scene model.</summary>
public sealed class DrawableWriter
{
    public required string Name { get; init; }
    public required IReadOnlyList<GMaterial> Materials { get; init; }
    public required IReadOnlyList<GBone> Bones { get; init; }
    public required GLod High { get; init; }
    public GLod? Medium { get; init; }
    public GLod? Low { get; init; }
    public float LodDistHigh { get; init; } = 100f;
    public float LodDistMedium { get; init; } = 200f;
    public float LodDistLow { get; init; } = 400f;
    /// <summary>Object-space bounds that also cover every animation frame (computed by the caller).</summary>
    public required Aabb Bounds { get; init; }
    /// <summary>Pre-rendered &lt;Bounds&gt; element or null for no embedded collision.</summary>
    public string? BoundsXml { get; init; }
    public bool WriteSkeleton { get; init; }
    /// <summary>"Drawable" for a .ydr, "Item" for an entry of a drawable dictionary (.ydd).</summary>
    public string RootTag { get; init; } = "Drawable";

    const int MaxVertsPerGeometry = 65535;

    public string Build()
    {
        var x = new Xw();
        x.Open(RootTag);
        x.Text("Name", Name);
        x.Vec3("BoundingSphereCenter", Bounds.Center);
        x.Value("BoundingSphereRadius", Bounds.Radius);
        x.Vec3("BoundingBoxMin", Bounds.Min);
        x.Vec3("BoundingBoxMax", Bounds.Max);
        x.Value("LodDistHigh", LodDistHigh);
        x.Value("LodDistMed", Medium != null ? LodDistMedium : 9998f);
        x.Value("LodDistLow", Low != null ? LodDistLow : 9998f);
        x.Value("LodDistVlow", 9998f);
        x.Value("FlagsHigh", 0);
        x.Value("FlagsMed", 0);
        x.Value("FlagsLow", 0);
        x.Value("FlagsVlow", 0);
        x.Value("Unknown9A", 9998);

        WriteShaderGroup(x);
        if (WriteSkeleton) WriteSkeletonXml(x);

        WriteModels(x, "DrawableModelsHigh", High);
        if (Medium != null) WriteModels(x, "DrawableModelsMedium", Medium);
        if (Low != null) WriteModels(x, "DrawableModelsLow", Low);

        if (BoundsXml != null) x.Fragment(BoundsXml);
        x.Empty("Lights");
        x.Close(RootTag);
        return x.ToString();
    }

    void WriteShaderGroup(Xw x)
    {
        x.Open("ShaderGroup");
        x.Value("Unknown30", 8);
        x.Open("Shaders");
        foreach (var m in Materials)
        {
            var def = Shaders.Get(m.Shader);
            int variant = (int)m.Alpha;
            x.Open("Item");
            x.Text("Name", def.Name);
            x.Text("FileName", def.Files[variant]);
            x.Value("RenderBucket", def.Buckets[variant]);
            x.Open("Parameters");
            foreach (var p in def.Params)
            {
                if (p.Type == "Texture")
                {
                    var tex = p.Name switch
                    {
                        "DiffuseSampler" => m.Diffuse,
                        "BumpSampler" => m.Normal,
                        "SpecSampler" => m.Specular,
                        _ => null,
                    } ?? m.Diffuse ?? "givemechecker";
                    x.Open("Item", $"name=\"{p.Name}\" type=\"Texture\"");
                    x.Text("Name", tex);
                    x.Close("Item");
                }
                else
                {
                    var v = p.Value;
                    if (p.Name == "emissiveMultiplier") v.X = m.EmissiveMultiplier;
                    if (p.Name == "bumpiness") v.X = m.Bumpiness;
                    if (p.Name == "specularIntensityMult" && m.Shader != "normal") v.X = m.SpecularIntensity;
                    x.Empty("Item", $"name=\"{p.Name}\" type=\"Vector\" x=\"{Xw.F(v.X)}\" y=\"{Xw.F(v.Y)}\" z=\"{Xw.F(v.Z)}\" w=\"{Xw.F(v.W)}\"");
                }
            }
            x.Close("Parameters");
            x.Close("Item");
        }
        x.Close("Shaders");
        x.Close("ShaderGroup");
    }

    void WriteSkeletonXml(Xw x)
    {
        var (u50, u54, u58) = SkeletonMath.Signatures(Name, Bones);
        x.Open("Skeleton");
        x.Value("Unknown1C", 16777216u);
        x.Value("Unknown50", u50);
        x.Value("Unknown54", u54);
        x.Value("Unknown58", u58);
        x.Open("Bones");
        for (int i = 0; i < Bones.Count; i++)
        {
            var b = Bones[i];
            int sibling = -1;
            for (int j = i + 1; j < Bones.Count; j++)
                if (Bones[j].Parent == b.Parent) { sibling = j; break; }
            bool hasChild = Bones.Any(o => o.Parent == i);
            var flags = "RotX, RotY, RotZ, TransX, TransY, TransZ" + (hasChild ? ", Unk0" : "");
            x.Open("Item");
            x.Text("Name", b.Name);
            x.Value("Tag", i == 0 ? 0 : SkeletonMath.BoneTag(b.Name));
            x.Value("Index", i);
            x.Value("ParentIndex", b.Parent);
            x.Value("SiblingIndex", sibling);
            x.Text("Flags", flags);
            x.Vec3("Translation", b.Translation);
            x.Quat("Rotation", b.Rotation);
            x.Vec3("Scale", b.Scale);
            x.Vec4("TransformUnk", new Vector4(0, 4, -3, 0));
            x.Close("Item");
        }
        x.Close("Bones");
        x.Close("Skeleton");
    }

    void WriteModels(Xw x, string tag, GLod lod)
    {
        x.Open(tag);
        foreach (var group in lod.Meshes.GroupBy(m => m.Bone).OrderBy(g => g.Key))
        {
            bool skinned = group.Any(m => m.BlendIndices != null);
            x.Open("Item");
            x.Value("RenderMask", 255);
            x.Value("Flags", skinned ? 1 : 0);
            x.Value("HasSkin", skinned ? 1 : 0);
            x.Value("BoneIndex", group.Key);
            x.Value("Unknown1", skinned ? Bones.Count : 0);
            x.Open("Geometries");
            foreach (var mesh in group)
                foreach (var chunk in Split(mesh))
                    WriteGeometry(x, chunk, skinned);
            x.Close("Geometries");
            x.Close("Item");
        }
        x.Close(tag);
    }

    void WriteGeometry(Xw x, GMesh m, bool skinned)
    {
        var def = Shaders.Get(Materials[m.Material].Shader);
        var layout = (skinned ? def.SkinnedLayout : def.RigidLayout).Split(' ');
        var tangents = layout.Contains("Tangent") ? MeshOps.Tangents(m) : null;
        var box = Aabb.Of(m.Positions);

        x.Open("Item");
        x.Value("ShaderIndex", m.Material);
        x.Vec3("BoundingBoxMin", box.Min);
        x.Vec3("BoundingBoxMax", box.Max);
        if (skinned)
            x.Raw("BoneIDs", [string.Join(", ", Enumerable.Range(0, Bones.Count))]);
        x.Open("VertexBuffer");
        x.Value("Flags", 0);
        x.Open("Layout", "type=\"GTAV1\"");
        foreach (var l in layout) x.Empty(l);
        x.Close("Layout");

        var rows = new string[m.Positions.Length];
        var c = CultureInfo.InvariantCulture;
        for (int v = 0; v < rows.Length; v++)
        {
            var cols = new List<string>(layout.Length);
            foreach (var l in layout)
            {
                switch (l)
                {
                    case "Position": cols.Add(V3(m.Positions[v])); break;
                    case "Normal": cols.Add(V3(m.Normals.Length > v ? m.Normals[v] : Vector3.UnitZ)); break;
                    case "Colour0": cols.Add(Bytes(m.Colors0, v, 255)); break;
                    case "Colour1": cols.Add(Bytes(m.Colors1, v, 0)); break;
                    case "TexCoord0": cols.Add(V2(m.Uv0.Length > v ? m.Uv0[v] : Vector2.Zero)); break;
                    case "TexCoord1": cols.Add(V2(m.Uv1 != null && m.Uv1.Length > v ? m.Uv1[v] : Vector2.Zero)); break;
                    case "Tangent":
                        var t = tangents![v];
                        cols.Add(string.Join(' ', Xw.F(t.X), Xw.F(t.Y), Xw.F(t.Z), Xw.F(t.W)));
                        break;
                    case "BlendWeights": cols.Add(Bytes(m.BlendWeights, v, 0, 255)); break;
                    case "BlendIndices": cols.Add(Bytes(m.BlendIndices, v, 0)); break;
                }
            }
            rows[v] = string.Join("   ", cols);
        }
        x.Raw("Data", rows);
        x.Close("VertexBuffer");
        x.Open("IndexBuffer");
        x.Raw("Data", m.Indices.Chunk(24).Select(ch => string.Join(' ', ch)));
        x.Close("IndexBuffer");
        x.Close("Item");

        static string V3(Vector3 p) => $"{Xw.F(p.X)} {Xw.F(p.Y)} {Xw.F(p.Z)}";
        static string V2(Vector2 p) => $"{Xw.F(p.X)} {Xw.F(p.Y)}";
        string Bytes(byte[]? src, int v, byte fill, byte firstFill = 0)
        {
            if (src == null)
                return firstFill != 0 ? $"{firstFill} 0 0 0" : $"{fill} {fill} {fill} {fill}";
            return string.Join(' ', src[v * 4].ToString(c), src[v * 4 + 1].ToString(c), src[v * 4 + 2].ToString(c), src[v * 4 + 3].ToString(c));
        }
    }

    /// <summary>Splits meshes over the 65535-vertex limit of a single geometry.</summary>
    static IEnumerable<GMesh> Split(GMesh m)
    {
        if (m.Positions.Length <= MaxVertsPerGeometry) { yield return m; yield break; }
        int tri = 0;
        int triCount = m.Indices.Length / 3;
        while (tri < triCount)
        {
            var map = new Dictionary<int, int>();
            var idx = new List<int>();
            while (tri < triCount && map.Count <= MaxVertsPerGeometry - 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int src = m.Indices[tri * 3 + k];
                    if (!map.TryGetValue(src, out int dst)) { dst = map.Count; map[src] = dst; }
                    idx.Add(dst);
                }
                tri++;
            }
            yield return MeshOps.Remap(m, map, idx.ToArray());
        }
    }
}
