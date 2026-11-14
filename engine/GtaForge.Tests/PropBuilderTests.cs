using System.IO.Compression;
using System.Numerics;
using GtaForge.Core.Gta;
using GtaForge.Core.Props;

namespace GtaForge.Tests;

public class PropBuilderTests
{
    internal static MeshRequest Cube(float size, Vector3 offset, int bone = 0, int material = 0)
    {
        var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
        Vector3[] dirs = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        foreach (var n in dirs)
        {
            var u = MathF.Abs(n.Z) > 0.5f ? Vector3.UnitX : Vector3.UnitZ;
            var v = Vector3.Cross(n, u);
            int b = pos.Count / 3;
            foreach (var (su, sv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var p = offset + (n + u * su + v * sv) * (size / 2);
                pos.AddRange([p.X, p.Y, p.Z]); nrm.AddRange([n.X, n.Y, n.Z]); uv.AddRange([(su + 1) / 2, (sv + 1) / 2]);
            }
            idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        return new MeshRequest { Bone = bone, Material = material, Positions = pos.ToArray(), Normals = nrm.ToArray(), Uv0 = uv.ToArray(), Indices = idx.ToArray() };
    }

    static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var za = new ZipArchive(new MemoryStream(zip));
        return za.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); });
    }

    [Theory]
    [InlineData("box")]
    [InlineData("mesh")]
    [InlineData("none")]
    public void Static_cube_builds_and_reads_back(string collision)
    {
        var req = new PropRequest
        {
            Name = "gf_test_cube",
            Collision = new CollisionRequest { Mode = collision, Material = 56 },
            Materials = [new MaterialRequest { Name = "paint", Color = [200, 30, 30, 255] }],
            Lods = new LodsRequest { High = [Cube(1, new Vector3(0, 0, 0.5f))] },
        };
        var result = PropBuilder.Build(req, new Dictionary<string, byte[]>());
        var files = Unzip(result.Resource.ToZip());

        Assert.Contains("gf_test_cube/stream/gf_test_cube.ydr", files.Keys);
        Assert.Contains("gf_test_cube/stream/gf_test_cube.ytd", files.Keys);
        Assert.Contains("gf_test_cube/stream/gf_test_cube.ytyp", files.Keys);
        Assert.DoesNotContain("gf_test_cube/stream/gf_test_cube.ycd", files.Keys);
        var manifest = System.Text.Encoding.UTF8.GetString(files["gf_test_cube/fxmanifest.lua"]);
        Assert.Contains("data_file 'DLC_ITYP_REQUEST' 'stream/gf_test_cube.ytyp'", manifest);

        var ydr = GtaIO.LoadYdr(files["gf_test_cube/stream/gf_test_cube.ydr"]);
        Assert.Equal(24, ydr.Drawable.DrawableModels.High[0].Geometries[0].VerticesCount);
        if (collision == "none") Assert.Null(ydr.Drawable.Bound);
        else Assert.NotNull(ydr.Drawable.Bound);

        var ytyp = GtaIO.LoadYtyp(files["gf_test_cube/stream/gf_test_cube.ytyp"]);
        var arch = ytyp.AllArchetypes[0]._BaseArchetypeDef;
        Assert.Equal((uint)ArchetypeFlags.UseAmbientScale, arch.flags);
        Assert.Equal(1f, arch.bbMax.Z, 0.001f);
        Assert.Equal(collision == "none" ? 0u : CodeWalker.GameFiles.JenkHash.GenHash("gf_test_cube"), arch.physicsDictionary.Hash);
    }

    [Fact]
    public void Textured_normal_spec_material_uses_normal_spec_shader()
    {
        var png = TextureWriter.SolidRgbaPng(10, 20, 30);
        var req = new PropRequest
        {
            Name = "gf_test_tex",
            Materials = [new MaterialRequest { Diffuse = "d", Normal = "n", Specular = "s" }],
            Textures = [new() { Name = "d", Kind = "diffuse", Field = "f0" }, new() { Name = "n", Kind = "normal", Field = "f1" }, new() { Name = "s", Kind = "specular", Field = "f2" }],
            Lods = new LodsRequest { High = [Cube(1, Vector3.Zero)] },
        };
        var result = PropBuilder.Build(req, new Dictionary<string, byte[]> { ["f0"] = png, ["f1"] = png, ["f2"] = png });
        var files = Unzip(result.Resource.ToZip());
        var ydr = GtaIO.LoadYdr(files["gf_test_tex/stream/gf_test_tex.ydr"]);
        var shader = ydr.Drawable.ShaderGroup.Shaders.data_items[0];
        Assert.Equal(CodeWalker.GameFiles.JenkHash.GenHash("normal_spec"), shader.Name.Hash);
        var ytd = GtaIO.LoadYtd(files["gf_test_tex/stream/gf_test_tex.ytd"]);
        Assert.Equal(3, ytd.TextureDict.Textures.data_items.Length);
    }

    [Fact]
    public void Animated_lid_writes_skeleton_clip_and_anim_flags()
    {
        // Two parts: a box base on the root bone and a lid on a "lid" bone that opens 90 degrees around X.
        int frames = 31;
        var rots = new List<float>();
        for (int f = 0; f < frames; f++)
        {
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2 * f / (frames - 1));
            rots.AddRange([q.X, q.Y, q.Z, q.W]);
        }
        var req = new PropRequest
        {
            Name = "gf_test_chest",
            Materials = [new MaterialRequest { Color = [120, 80, 40, 255] }],
            Bones = [new BoneRequest { Name = "root" }, new BoneRequest { Name = "lid", Parent = 0, T = [0, 0.5f, 1f] }],
            Lods = new LodsRequest { High = [Cube(1, new Vector3(0, 0, 0.5f)), Cube(1, new Vector3(0, -0.5f, 0.05f), bone: 1)] },
            Animations = [new AnimationRequest { Name = "open", Fps = 30, FrameCount = frames, Tracks = [new TrackRequest { Bone = 1, R = rots.ToArray() }] }],
        };
        var result = PropBuilder.Build(req, new Dictionary<string, byte[]>());
        var files = Unzip(result.Resource.ToZip());

        var ydr = GtaIO.LoadYdr(files["gf_test_chest/stream/gf_test_chest.ydr"]);
        Assert.Equal(2, ydr.Drawable.Skeleton.Bones.Items.Length);
        Assert.Equal(SkeletonMath.BoneTag("lid"), ydr.Drawable.Skeleton.Bones.Items[1].Tag);
        Assert.Equal(2, ydr.Drawable.DrawableModels.High.Length);
        Assert.Equal(1, ydr.Drawable.DrawableModels.High[1].BoneIndex);
        Assert.NotEqual(0u, ydr.Drawable.Skeleton.Unknown_50h.Hash);

        var ycd = GtaIO.LoadYcd(files["gf_test_chest/stream/gf_test_chest.ycd"]);
        Assert.Single(ycd.ClipMap);
        Assert.Single(ycd.AnimMap);
        var anim = ycd.AnimMap.Values.First().Animation;
        Assert.Equal(frames, anim.Frames);
        Assert.Equal(SkeletonMath.BoneTag("lid"), anim.BoneIds.data_items[0].BoneId);

        var arch = GtaIO.LoadYtyp(files["gf_test_chest/stream/gf_test_chest.ytyp"]).AllArchetypes[0]._BaseArchetypeDef;
        Assert.True((arch.flags & (uint)ArchetypeFlags.HasAnim) != 0);
        Assert.True((arch.flags & (uint)ArchetypeFlags.AutoStartAnim) != 0);
        Assert.Equal(CodeWalker.GameFiles.JenkHash.GenHash("gf_test_chest"), arch.clipDictionary.Hash);
        // The open lid swings to y = -0.5 - 1 + ... ; bounds must include the swept volume (above the base).
        Assert.True(arch.bbMax.Z > 1.5f, $"bbMax.Z = {arch.bbMax.Z}, expected the bounds to include the opened lid");
    }
}

public class AnimationDecodeTests
{
    [Fact]
    public void Clip_values_decode_to_the_requested_rotation()
    {
        int frames = 11;
        var rots = new List<float>();
        for (int f = 0; f < frames; f++)
        {
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2 * f / (frames - 1));
            rots.AddRange([q.X, q.Y, q.Z, q.W]);
        }
        var bones = new List<GBone> { new() { Name = "root" }, new() { Name = "lid", Parent = 0 } };
        var anim = new GAnimation { Name = "open", FrameCount = frames, Duration = 1f, Tracks = [new GTrack { Bone = 1, Rotations = Core.Props.RequestMapping.Q(rots.ToArray()) }] };
        var ycd = GtaIO.LoadYcd(YcdWriter.Build(bones, [anim]));
        var xml = CodeWalker.GameFiles.YcdXml.GetXml(ycd);
        // Last frame of the w channel must be cos(45deg) = 0.7071 and x = -0.7071.
        var a = ycd.AnimMap.Values.First().Animation;
        int bi = a.FindBoneIndex(SkeletonMath.BoneTag("lid"), 1);
        Assert.True(bi >= 0);
        var first = a.EvaluateQuaternion(new CodeWalker.GameFiles.Animation.FramePosition { Frame0 = 0, Alpha0 = 1 }, bi, false);
        var last = a.EvaluateQuaternion(new CodeWalker.GameFiles.Animation.FramePosition { Frame0 = frames - 1, Alpha0 = 1 }, bi, false);
        Assert.Equal(0f, first.X, 0.002f);
        Assert.Equal(1f, first.W, 0.002f);
        Assert.Equal(-0.7071f, last.X, 0.002f);
        Assert.Equal(0.7071f, last.W, 0.002f);
        Assert.Contains("QuantizeFloat", xml);
    }
}
