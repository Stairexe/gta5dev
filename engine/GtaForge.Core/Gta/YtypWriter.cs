namespace GtaForge.Core.Gta;

/// <summary>Archetype flags (CBaseArchetypeDef.flags).</summary>
[Flags]
public enum ArchetypeFlags : uint
{
    None = 0,
    DontFade = 4,
    DrawLast = 8,
    Static = 64,
    HasAnim = 512,
    UvAnims = 1024,
    DontCastShadows = 8192,
    DoubleSided = 65536,
    Dynamic = 131072,
    AutoStartAnim = 524288,
    UseAmbientScale = 536870912,
}

public sealed class ArchetypeSpec
{
    public required string Name { get; init; }
    public string? TextureDictionary { get; init; }
    public string? ClipDictionary { get; init; }
    public bool HasPhysics { get; init; }
    public required Aabb Bounds { get; init; }
    public float LodDist { get; init; } = 100f;
    public float HdTextureDist { get; init; } = 60f;
    public ArchetypeFlags Flags { get; init; } = ArchetypeFlags.UseAmbientScale;
    public uint SpecialAttribute { get; init; }
}

/// <summary>Writes .ytyp (CMapTypes) XML for drawable archetypes and compiles it to an RSC meta.</summary>
public static class YtypWriter
{
    public static string BuildXml(string typesName, IEnumerable<ArchetypeSpec> archetypes)
    {
        var x = new Xw();
        x.Open("CMapTypes");
        x.Empty("extensions");
        x.Open("archetypes");
        foreach (var a in archetypes)
        {
            x.Open("Item", "type=\"CBaseArchetypeDef\"");
            x.Value("lodDist", a.LodDist);
            x.Value("flags", (uint)a.Flags);
            x.Value("specialAttribute", a.SpecialAttribute);
            x.Vec3("bbMin", a.Bounds.Min);
            x.Vec3("bbMax", a.Bounds.Max);
            x.Vec3("bsCentre", a.Bounds.Center);
            x.Value("bsRadius", a.Bounds.Radius);
            x.Value("hdTextureDist", a.HdTextureDist);
            x.Text("name", a.Name);
            x.Text("textureDictionary", a.TextureDictionary ?? "");
            x.Text("clipDictionary", a.ClipDictionary ?? "");
            x.Text("drawableDictionary", "");
            // Never empty for a prop with collision: an empty physicsDictionary renders the model
            // but lets players walk through it. Use the prop's own name.
            x.Text("physicsDictionary", a.HasPhysics ? a.Name : "");
            x.Text("assetType", "ASSET_TYPE_DRAWABLE");
            x.Text("assetName", a.Name);
            x.Empty("extensions");
            x.Close("Item");
        }
        x.Close("archetypes");
        x.Text("name", typesName);
        x.Empty("dependencies");
        x.Empty("compositeEntityTypes");
        x.Close("CMapTypes");
        return x.ToString();
    }

    public static byte[] Build(string typesName, IEnumerable<ArchetypeSpec> archetypes) =>
        GtaIO.RscMetaFromXml(BuildXml(typesName, archetypes));
}
