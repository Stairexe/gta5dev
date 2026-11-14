using System.IO.Compression;
using System.Numerics;
using CodeWalker.GameFiles;
using GtaForge.Engine.Gta;
using GtaForge.Engine.Peds;
using GtaForge.Engine.Props;

namespace GtaForge.Tests;

public class PedBuilderTests
{
    static readonly string[] BoneNames =
        ["SKEL_ROOT", "SKEL_Pelvis", "SKEL_Spine0", "SKEL_Neck_1", "SKEL_Head", "SKEL_L_Thigh", "SKEL_L_Foot", "SKEL_R_Thigh", "SKEL_R_Foot"];

    /// <summary>A crude rigged "ped": one skinned mesh, each box fully weighted to one bone.</summary>
    internal static byte[] SyntheticPedYdd(string drawableName = "body", bool singleBody = true)
    {
        var bones = BoneNames.Select((n, i) => new GBone { Name = n, Parent = i == 0 ? -1 : n switch
        {
            "SKEL_Pelvis" => 0, "SKEL_Spine0" => 1, "SKEL_Neck_1" => 2, "SKEL_Head" => 3,
            "SKEL_L_Thigh" or "SKEL_R_Thigh" => 1, "SKEL_L_Foot" => 5, "SKEL_R_Foot" => 7, _ => 0,
        } }).ToList();
        var parts = new (string bone, Vector3 at, float size)[]
        {
            ("SKEL_Head", new(0, 0, 1.7f), 0.25f), ("SKEL_Spine0", new(0, 0, 1.3f), 0.45f), ("SKEL_Pelvis", new(0, 0, 0.95f), 0.35f),
            ("SKEL_L_Thigh", new(0.12f, 0, 0.55f), 0.18f), ("SKEL_R_Thigh", new(-0.12f, 0, 0.55f), 0.18f),
            ("SKEL_L_Foot", new(0.12f, 0.05f, 0.08f), 0.15f), ("SKEL_R_Foot", new(-0.12f, 0.05f, 0.08f), 0.15f),
        };
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
        var bi = new List<byte>(); var bw = new List<byte>();
        foreach (var (bone, at, size) in parts)
        {
            var cube = PropBuilderTests.Cube(size, at);
            int b = pos.Count;
            pos.AddRange(Engine.Props.RequestMapping.V3(cube.Positions));
            nrm.AddRange(Engine.Props.RequestMapping.V3(cube.Normals!));
            uv.AddRange(Engine.Props.RequestMapping.V2(cube.Uv0!));
            idx.AddRange(cube.Indices.Select(i => i + b));
            byte bIndex = (byte)Array.IndexOf(BoneNames, bone);
            for (int v = 0; v < cube.Positions.Length / 3; v++) { bi.AddRange([bIndex, 0, 0, 0]); bw.AddRange([255, 0, 0, 0]); }
        }
        var mesh = new GMesh
        {
            Positions = pos.ToArray(), Normals = nrm.ToArray(), Uv0 = uv.ToArray(), Uv1 = uv.ToArray(),
            BlendIndices = bi.ToArray(), BlendWeights = bw.ToArray(), Indices = idx.ToArray(),
            Colors0 = Enumerable.Range(0, pos.Count).SelectMany(_ => new byte[] { 255, 128, 0, 255 }).ToArray(),
            Colors1 = new byte[pos.Count * 4],
        };
        var item = new DrawableWriter
        {
            Name = drawableName,
            RootTag = "Item",
            Materials = [new GMaterial { Shader = "ped", Diffuse = "body_diff", Normal = "body_nrm", Specular = "body_spec" }],
            Bones = bones,
            High = new GLod { Meshes = [mesh] },
            Bounds = Aabb.Of(pos),
            WriteSkeleton = true,
        }.Build();
        item = item[(item.IndexOf("?>") + 2)..];
        return GtaIO.YddFromXml($"<DrawableDictionary>{item}</DrawableDictionary>");
    }

    static byte[] BodyYtd()
    {
        var png = TextureWriter.SolidRgbaPng(200, 150, 120);
        return TextureWriter.BuildYtd([new("body_diff", png, TextureKind.Diffuse), new("body_nrm", png, TextureKind.Normal), new("body_spec", png, TextureKind.Specular)],
            Directory.CreateTempSubdirectory("gf_t_").FullName);
    }

    static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var za = new ZipArchive(new MemoryStream(zip));
        return za.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); });
    }

    [Fact]
    public void Full_body_ydd_is_split_into_components_with_convention_textures()
    {
        var result = PedBuilder.Build(new PedRequest { Name = "gf_test_ped" }, new PedInput(SyntheticPedYdd(), BodyYtd(), null));
        var files = Unzip(result.Resource.ToZip());

        var ydd = GtaIO.LoadYdd(files["gf_test_ped/stream/gf_test_ped.ydd"]);
        var hashes = ydd.DrawableDict.Hashes;
        var expected = new[] { "head_000_u", "uppr_000_u", "lowr_000_u", "feet_000_u" }.Select(JenkHash.GenHash).OrderBy(h => h).ToArray();
        Assert.Equal(expected, hashes);
        Assert.True(hashes.SequenceEqual(hashes.OrderBy(h => h)), "drawable hashes must be sorted");

        // Every component drawable carries the skeleton with unique, non-zero signatures.
        foreach (var d in ydd.Drawables)
        {
            Assert.Equal(BoneNames.Length, d.Skeleton.Bones.Items.Length);
            Assert.NotEqual(0u, d.Skeleton.Unknown_50h.Hash);
        }
        // Head piece = one cube = 24 verts; feet = two cubes = 48; lowr = pelvis + 2 thighs = 72.
        int Verts(string n) => ydd.Dict[JenkHash.GenHash(n)].DrawableModels.High.Sum(m => m.Geometries.Sum(g => (int)g.VerticesCount));
        Assert.Equal(24, Verts("head_000_u"));
        Assert.Equal(24, Verts("uppr_000_u"));
        Assert.Equal(72, Verts("lowr_000_u"));
        Assert.Equal(48, Verts("feet_000_u"));

        var ytd = GtaIO.LoadYtd(files["gf_test_ped/stream/gf_test_ped.ytd"]);
        var texNames = ytd.TextureDict.Textures.data_items.Select(t => t.Name).ToHashSet();
        foreach (var c in new[] { "head", "uppr", "lowr", "feet" })
        {
            Assert.Contains($"{c}_diff_000_a_uni", texNames);
            Assert.Contains($"{c}_normal_000", texNames);
            Assert.Contains($"{c}_spec_000", texNames);
        }

        var ymt = GtaIO.LoadYmt(files["gf_test_ped/stream/gf_test_ped.ymt"]);
        Assert.NotNull(ymt.Pso);
        var ymtXml = MetaXml.GetXml(ymt, out _);
        Assert.Contains("CPedVariationInfo", ymtXml);
        var flat = string.Join(' ', ymtXml.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("<availComp> 0 255 255 1 2 255 3 255 255 255 255 255 </availComp>", flat);
        Assert.Contains("<pedXml_vfxComps>PV_COMP_FEET</pedXml_vfxComps>", flat);
        Assert.Contains("<pedXml_compIdx value=\"6\" />", flat);

        var yft = GtaIO.LoadYft(files["gf_test_ped/stream/gf_test_ped.yft"]);
        Assert.Equal(BoneNames.Length, yft.Fragment.Drawable.Skeleton.Bones.Items.Length);

        var meta = System.Text.Encoding.UTF8.GetString(files["gf_test_ped/peds.meta"]);
        Assert.Contains("<Name>gf_test_ped</Name>", meta);
        var manifest = System.Text.Encoding.UTF8.GetString(files["gf_test_ped/fxmanifest.lua"]);
        Assert.Contains("data_file 'PED_METADATA_FILE' 'peds.meta'", manifest);
        Assert.Empty(result.Report.Warnings.Where(w => w.Contains("placeholder")));
    }

    [Fact]
    public void Already_split_ydd_is_kept_and_textures_fall_back_to_placeholders()
    {
        var result = PedBuilder.Build(new PedRequest { Name = "gf_test_ped2" }, new PedInput(SyntheticPedYdd("uppr_000_u"), null, null));
        var files = Unzip(result.Resource.ToZip());
        var ydd = GtaIO.LoadYdd(files["gf_test_ped2/stream/gf_test_ped2.ydd"]);
        Assert.Equal([JenkHash.GenHash("uppr_000_u")], ydd.DrawableDict.Hashes);
        Assert.Contains(result.Report.Warnings, w => w.Contains("placeholder"));
        Assert.Contains(result.Report.Warnings, w => w.Contains("skeleton-only .yft"));
    }
}

public class FixtureWriter
{
    /// <summary>Writes the synthetic ped (.ydd + .ytd) to GF_FIXTURE_DIR for browser tests. No-op otherwise.</summary>
    [Fact]
    public void Write_synthetic_ped_fixture_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("GF_FIXTURE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "test_body.ydd"), PedBuilderTests.SyntheticPedYdd());
        var png = TextureWriter.SolidRgbaPng(200, 150, 120);
        File.WriteAllBytes(Path.Combine(dir, "test_body.ytd"), TextureWriter.BuildYtd(
            [new("body_diff", png, TextureKind.Diffuse), new("body_nrm", png, TextureKind.Normal), new("body_spec", png, TextureKind.Specular)],
            Directory.CreateTempSubdirectory("gf_fx_").FullName));
    }
}
