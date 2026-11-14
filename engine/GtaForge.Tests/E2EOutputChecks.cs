using System.IO.Compression;
using System.Numerics;
using GtaForge.Engine.Gta;

namespace GtaForge.Tests;

/// <summary>Checks a browser-exported animated chest (zip path in GF_E2E_CHEST). No-op when unset.</summary>
public class E2EOutputChecks
{
    [Fact]
    public void Browser_exported_chest_has_lid_in_place_and_opening_clip()
    {
        var zipPath = Environment.GetEnvironmentVariable("GF_E2E_CHEST");
        if (string.IsNullOrEmpty(zipPath)) return;
        using var za = ZipFile.OpenRead(zipPath);
        byte[] Read(string n) { using var s = za.GetEntry(n)!.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
        var ydr = GtaIO.LoadYdr(Read("gf_chest/stream/gf_chest.ydr"));
        var bones = ydr.Drawable.Skeleton.Bones.Items;
        Assert.Equal("lid", bones[1].Name);
        // Rest transform of the lid bone: hinge at the back-top edge (GTA y = +0.3, z = 0.6).
        Assert.Equal(0.3f, bones[1].Translation.Y, 0.01f);
        Assert.Equal(0.6f, bones[1].Translation.Z, 0.01f);
        // Lid vertices in bone space + bone translation = object space: lid spans y -0.3..0.3, z 0.6..0.72.
        var preview = Engine.Inspect.Inspector.Inspect("c.ydr", Read("gf_chest/stream/gf_chest.ydr"));
        var lidMesh = preview.Meshes[1];
        var t = new Vector3(bones[1].Translation.X, bones[1].Translation.Y, bones[1].Translation.Z);
        float minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < lidMesh.Positions.Length; i += 3)
        {
            var p = new Vector3(lidMesh.Positions[i], lidMesh.Positions[i + 1], lidMesh.Positions[i + 2]) + t;
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }
        Assert.Equal(-0.3f, minY, 0.01f); Assert.Equal(0.3f, maxY, 0.01f);
        Assert.Equal(0.6f, minZ, 0.01f); Assert.Equal(0.72f, maxZ, 0.01f);

        var ycd = GtaIO.LoadYcd(Read("gf_chest/stream/gf_chest.ycd"));
        var a = ycd.AnimMap.Values.First().Animation;
        int bi = a.FindBoneIndex(SkeletonMath.BoneTag("lid"), 1);
        Assert.True(bi >= 0);
        var mid = a.EvaluateQuaternion(new CodeWalker.GameFiles.Animation.FramePosition { Frame0 = 30, Alpha0 = 1 }, bi, false);
        var q = new Quaternion(mid.X, mid.Y, mid.Z, mid.W);
        var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0, 1)) * 180 / MathF.PI;
        Assert.Equal(100f, angle, 1.5f);
        Assert.True(MathF.Abs(q.X) > 0.7f, $"rotation should be about X, got {q}");
    }
}
