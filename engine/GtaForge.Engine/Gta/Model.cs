using System.Numerics;

namespace GtaForge.Engine.Gta;

// Neutral, GTA-axis (Z-up) scene description that the writers turn into CodeWalker XML.
// The browser converts glTF (Y-up) into this space before upload.

public enum AlphaMode { Opaque, Cutout, Blend }

public sealed class GMaterial
{
    public string Name { get; set; } = "material";
    /// <summary>default | normal | normal_spec | spec | emissive</summary>
    public string Shader { get; set; } = "default";
    public AlphaMode Alpha { get; set; } = AlphaMode.Opaque;
    public string? Diffuse { get; set; }
    public string? Normal { get; set; }
    public string? Specular { get; set; }
    public float EmissiveMultiplier { get; set; } = 1f;
    public float Bumpiness { get; set; } = 1f;
    public float SpecularIntensity { get; set; } = 1f;
}

public sealed class GBone
{
    public string Name { get; set; } = "root";
    public int Parent { get; set; } = -1;
    public Vector3 Translation { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;
}

public sealed class GMesh
{
    public int Material { get; set; }
    /// <summary>Bone the mesh is rigidly attached to. Vertices are in that bone's space.</summary>
    public int Bone { get; set; }
    public Vector3[] Positions { get; set; } = [];
    public Vector3[] Normals { get; set; } = [];
    public Vector2[] Uv0 { get; set; } = [];
    public Vector2[]? Uv1 { get; set; }
    /// <summary>RGBA 0-255 per vertex, optional (defaults to white).</summary>
    public byte[]? Colors0 { get; set; }
    public byte[]? Colors1 { get; set; }
    /// <summary>Skinning: 4 bone indices (into the skeleton) and 4 weights 0-255 per vertex.</summary>
    public byte[]? BlendIndices { get; set; }
    public byte[]? BlendWeights { get; set; }
    public int[] Indices { get; set; } = [];
}

public sealed class GLod
{
    public List<GMesh> Meshes { get; set; } = [];
}

public sealed class GTrack
{
    public int Bone { get; set; }
    /// <summary>One entry per frame (or empty when the bone does not translate).</summary>
    public Vector3[]? Translations { get; set; }
    public Quaternion[]? Rotations { get; set; }
}

public sealed class GAnimation
{
    public string Name { get; set; } = "anim";
    public int FrameCount { get; set; }
    public float Duration { get; set; }
    public List<GTrack> Tracks { get; set; } = [];
}

public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    public static readonly Aabb Empty = new(new Vector3(float.MaxValue), new Vector3(float.MinValue));
    public bool IsEmpty => Min.X > Max.X;
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Size => Max - Min;
    public float Radius => IsEmpty ? 0 : (Max - Min).Length() * 0.5f;
    public Aabb Include(Vector3 p) => new(Vector3.Min(Min, p), Vector3.Max(Max, p));
    public Aabb Include(Aabb o) => o.IsEmpty ? this : IsEmpty ? o : new(Vector3.Min(Min, o.Min), Vector3.Max(Max, o.Max));
    public static Aabb Of(IEnumerable<Vector3> pts) => pts.Aggregate(Empty, (a, p) => a.Include(p));
}
