using System.Numerics;
using GtaForge.Core.Gta;

namespace GtaForge.Core.Props;

// JSON contract sent by the browser. Geometry is already in GTA space (Z-up, metres),
// flattened into plain arrays to keep the payload compact.

public sealed class PropRequest
{
    public string Name { get; set; } = "my_prop";
    /// <summary>Draw distance in metres (archetype lodDist).</summary>
    public float LodDist { get; set; } = 150f;
    /// <summary>High to Medium switch distance; 0 = auto (35% of LodDist).</summary>
    public float LodDistMedium { get; set; }
    /// <summary>Medium to Low switch distance; 0 = auto (70% of LodDist).</summary>
    public float LodDistLow { get; set; }
    public bool Dynamic { get; set; }
    public CollisionRequest Collision { get; set; } = new();
    public List<MaterialRequest> Materials { get; set; } = [];
    public List<TextureRequest> Textures { get; set; } = [];
    public List<BoneRequest> Bones { get; set; } = [];
    public LodsRequest Lods { get; set; } = new();
    public List<AnimationRequest> Animations { get; set; } = [];
    /// <summary>Add a /spawn test command to the resource.</summary>
    public bool TestCommand { get; set; } = true;
}

public sealed class CollisionRequest
{
    /// <summary>none | box | mesh</summary>
    public string Mode { get; set; } = "box";
    public int Material { get; set; }
    /// <summary>Optional simplified collision mesh (object space). Falls back to the high LOD.</summary>
    public float[]? Positions { get; set; }
    public int[]? Indices { get; set; }
}

public sealed class MaterialRequest
{
    public string Name { get; set; } = "material";
    public string Shader { get; set; } = "default";
    public string Alpha { get; set; } = "opaque";
    public string? Diffuse { get; set; }
    public string? Normal { get; set; }
    public string? Specular { get; set; }
    public float EmissiveMultiplier { get; set; } = 1f;
    /// <summary>Base colour 0-255 RGBA, used to generate a texture when there is no diffuse map.</summary>
    public int[]? Color { get; set; }
}

public sealed class TextureRequest
{
    public string Name { get; set; } = "";
    /// <summary>diffuse | normal | specular</summary>
    public string Kind { get; set; } = "diffuse";
    /// <summary>Multipart form field that carries the image file.</summary>
    public string Field { get; set; } = "";
}

public sealed class BoneRequest
{
    public string Name { get; set; } = "root";
    public int Parent { get; set; } = -1;
    public float[] T { get; set; } = [0, 0, 0];
    public float[] R { get; set; } = [0, 0, 0, 1];
    public float[] S { get; set; } = [1, 1, 1];
}

public sealed class LodsRequest
{
    public List<MeshRequest> High { get; set; } = [];
    public List<MeshRequest>? Medium { get; set; }
    public List<MeshRequest>? Low { get; set; }
}

public sealed class MeshRequest
{
    public int Material { get; set; }
    public int Bone { get; set; }
    public float[] Positions { get; set; } = [];
    public float[]? Normals { get; set; }
    public float[]? Uv0 { get; set; }
    /// <summary>RGBA 0-255 per vertex, flattened.</summary>
    public int[]? Colors { get; set; }
    public int[] Indices { get; set; } = [];
}

public sealed class AnimationRequest
{
    public string Name { get; set; } = "anim";
    public float Fps { get; set; } = 30f;
    public int FrameCount { get; set; }
    public List<TrackRequest> Tracks { get; set; } = [];
}

public sealed class TrackRequest
{
    public int Bone { get; set; }
    /// <summary>Local translation per frame, flattened xyz.</summary>
    public float[]? T { get; set; }
    /// <summary>Local rotation per frame, flattened xyzw.</summary>
    public float[]? R { get; set; }
}

public static class RequestMapping
{
    public static GBone ToBone(this BoneRequest b) => new()
    {
        Name = b.Name,
        Parent = b.Parent,
        Translation = new Vector3(b.T[0], b.T[1], b.T[2]),
        Rotation = Quaternion.Normalize(new Quaternion(b.R[0], b.R[1], b.R[2], b.R[3])),
        Scale = b.S is { Length: 3 } ? new Vector3(b.S[0], b.S[1], b.S[2]) : Vector3.One,
    };

    public static GMesh ToMesh(this MeshRequest m)
    {
        int n = m.Positions.Length / 3;
        if (m.Indices.Any(i => i < 0 || i >= n))
            throw new ArgumentException("Mesh index out of range.");
        return new GMesh
        {
            Material = m.Material,
            Bone = m.Bone,
            Positions = V3(m.Positions),
            Normals = m.Normals is { } nr && nr.Length == n * 3 ? V3(nr).Select(SafeNormal).ToArray() : [],
            Uv0 = m.Uv0 is { } uv && uv.Length == n * 2 ? V2(uv) : new Vector2[n],
            Colors0 = m.Colors is { } c && c.Length == n * 4 ? c.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray() : null,
            Indices = m.Indices,
        };
    }

    static Vector3 SafeNormal(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;

    public static Vector3[] V3(float[] f)
    {
        var r = new Vector3[f.Length / 3];
        for (int i = 0; i < r.Length; i++) r[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
        return r;
    }

    public static Vector2[] V2(float[] f)
    {
        var r = new Vector2[f.Length / 2];
        for (int i = 0; i < r.Length; i++) r[i] = new Vector2(f[i * 2], f[i * 2 + 1]);
        return r;
    }

    public static Quaternion[] Q(float[] f)
    {
        var r = new Quaternion[f.Length / 4];
        for (int i = 0; i < r.Length; i++)
            r[i] = Quaternion.Normalize(new Quaternion(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]));
        return r;
    }
}
