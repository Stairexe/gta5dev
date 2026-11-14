using System.Numerics;

namespace GtaForge.Core.Gta;

/// <summary>
/// Shader presets for props. Parameter lists and defaults follow the vanilla shader
/// definitions (the same lists Sollumz/CodeWalker use); the game expects every parameter
/// of the .sps to be present, in this order.
/// </summary>
public static class Shaders
{
    public sealed record Param(string Name, string Type, Vector4 Value);

    public sealed record Def(
        string Name,
        string[] Files,          // [opaque, cutout, alpha] .sps; null entries = unsupported
        int[] Buckets,           // render bucket per file
        string RigidLayout,
        string SkinnedLayout,
        Param[] Params);

    static Param Tex(string n) => new(n, "Texture", default);
    static Param V(string n, float x, float y = 0, float z = 0, float w = 0) => new(n, "Vector", new Vector4(x, y, z, w));

    public static readonly Def Default = new("default",
        ["default.sps", "cutout.sps", "alpha.sps"], [0, 3, 1],
        "Position Normal Colour0 TexCoord0",
        "Position BlendWeights BlendIndices Normal Colour0 TexCoord0",
        [
            Tex("DiffuseSampler"),
            V("matMaterialColorScale", 1, 0, 0, 1),
            V("HardAlphaBlend", 1),
            V("useTessellation", 0),
            V("wetnessMultiplier", 1),
            V("globalAnimUV1", 0, 1, 0),
            V("globalAnimUV0", 1, 0, 0),
        ]);

    public static readonly Def Normal = new("normal",
        ["normal.sps", "normal_cutout.sps", "normal_alpha.sps"], [0, 3, 1],
        "Position Normal Colour0 TexCoord0 Tangent",
        "Position BlendWeights BlendIndices Normal Colour0 TexCoord0 Tangent",
        [
            Tex("DiffuseSampler"),
            Tex("BumpSampler"),
            V("HardAlphaBlend", 1),
            V("useTessellation", 0),
            V("wetnessMultiplier", 1),
            V("bumpiness", 1),
            V("specularIntensityMult", 0.125f),
            V("specularFalloffMult", 100),
            V("specularFresnel", 0.75f),
            V("globalAnimUV1", 0, 1, 0),
            V("globalAnimUV0", 1, 0, 0),
        ]);

    public static readonly Def NormalSpec = new("normal_spec",
        ["normal_spec.sps", "normal_spec_cutout.sps", "normal_spec_alpha.sps"], [0, 3, 1],
        "Position Normal Colour0 TexCoord0 Tangent",
        "Position BlendWeights BlendIndices Normal Colour0 TexCoord0 Tangent",
        [
            Tex("DiffuseSampler"),
            Tex("BumpSampler"),
            Tex("SpecSampler"),
            V("HardAlphaBlend", 1),
            V("useTessellation", 0),
            V("wetnessMultiplier", 1),
            V("bumpiness", 1),
            V("specMapIntMask", 1, 0, 0),
            V("specularIntensityMult", 1),
            V("specularFalloffMult", 100),
            V("specularFresnel", 0.75f),
        ]);

    public static readonly Def Spec = new("spec",
        ["spec.sps", "spec_screendooralpha.sps", "spec_alpha.sps"], [0, 3, 1],
        "Position Normal Colour0 TexCoord0",
        "Position BlendWeights BlendIndices Normal Colour0 TexCoord0",
        [
            Tex("DiffuseSampler"),
            Tex("SpecSampler"),
            V("HardAlphaBlend", 1),
            V("useTessellation", 0),
            V("wetnessMultiplier", 1),
            V("specMapIntMask", 1, 0, 0),
            V("specularIntensityMult", 1),
            V("specularFalloffMult", 100),
            V("specularFresnel", 0.97f),
            V("globalAnimUV1", 0, 1, 0),
            V("globalAnimUV0", 1, 0, 0),
        ]);

    public static readonly Def Emissive = new("emissive",
        ["emissive.sps", "emissive.sps", "emissive_alpha.sps"], [0, 0, 1],
        "Position Normal Colour0 TexCoord0",
        "Position BlendWeights BlendIndices Normal Colour0 TexCoord0",
        [
            Tex("DiffuseSampler"),
            V("matMaterialColorScale", 1, 0, 0, 1),
            V("HardAlphaBlend", 1),
            V("useTessellation", 0),
            V("emissiveMultiplier", 1),
            V("globalAnimUV1", 0, 1, 0),
            V("globalAnimUV0", 1, 0, 0),
        ]);

    /// <summary>Standard ped skin/clothing shader (skinned, two vertex colours, two UVs).</summary>
    public static readonly Def Ped = new("ped",
        ["ped.sps", "ped.sps", "ped_alpha.sps"], [0, 0, 1],
        "Position Normal Colour0 Colour1 TexCoord0 TexCoord1 Tangent",
        "Position BlendWeights BlendIndices Normal Colour0 Colour1 TexCoord0 TexCoord1 Tangent",
        [
            Tex("DiffuseSampler"),
            Tex("BumpSampler"),
            Tex("SpecSampler"),
            V("umGlobalParams", 0.0025f, 0.0025f, 7, 7),
            V("envEffFatThickness", 25, 25),
            V("specularIntensityMult", 1),
            V("specularFalloffMult", 250),
            V("specularFresnel", 0.96f),
            V("bumpiness", 1),
            V("detailSettings", 0.1f, 0.75f, 40),
            V("StubbleControl", 2, 0.6f),
        ]);

    public static Def Get(string name) => name switch
    {
        "ped" => Ped,
        "normal" => Normal,
        "normal_spec" => NormalSpec,
        "spec" => Spec,
        "emissive" => Emissive,
        _ => Default,
    };

    public static bool NeedsTangents(Def d) => d.RigidLayout.Contains("Tangent");
}
