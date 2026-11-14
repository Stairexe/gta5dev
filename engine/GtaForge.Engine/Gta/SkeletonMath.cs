using System.IO.Hashing;
using System.Numerics;
using System.Text;
using CodeWalker.GameFiles;

namespace GtaForge.Engine.Gta;

public static class SkeletonMath
{
    /// <summary>Bone tag for non-root bones (ElfHash of the upper-case name, folded into the 0x170..0xFFFF range).</summary>
    public static ushort BoneTag(string name)
    {
        uint h = 0;
        foreach (char c in name.ToUpperInvariant())
        {
            h = (h << 4) + c;
            uint x = h & 0xF0000000;
            if (x != 0) h ^= x >> 24;
            h &= ~x;
        }
        return (ushort)(h % 0xFE8F + 0x170);
    }

    public static Matrix4x4 Local(GBone b) =>
        Matrix4x4.CreateScale(b.Scale) * Matrix4x4.CreateFromQuaternion(b.Rotation) * Matrix4x4.CreateTranslation(b.Translation);

    public static Matrix4x4 Local(Vector3 t, Quaternion r, Vector3 s) =>
        Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);

    /// <summary>Object-space (model-space) transform of every bone in its rest pose.</summary>
    public static Matrix4x4[] Globals(IReadOnlyList<GBone> bones)
    {
        var g = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            var local = Local(bones[i]);
            g[i] = bones[i].Parent >= 0 ? local * g[bones[i].Parent] : local;
        }
        return g;
    }

    /// <summary>Object-space bone transforms at one animation frame (bones without a track keep their rest pose).</summary>
    public static Matrix4x4[] GlobalsAtFrame(IReadOnlyList<GBone> bones, GAnimation anim, int frame)
    {
        var g = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            var t = b.Translation;
            var r = b.Rotation;
            var tr = anim.Tracks.FirstOrDefault(x => x.Bone == i);
            if (tr?.Translations is { Length: > 0 } ts) t = ts[Math.Min(frame, ts.Length - 1)];
            if (tr?.Rotations is { Length: > 0 } rs) r = rs[Math.Min(frame, rs.Length - 1)];
            var local = Local(t, r, b.Scale);
            g[i] = b.Parent >= 0 ? local * g[b.Parent] : local;
        }
        return g;
    }

    /// <summary>
    /// The skeleton "signature" fields. Their real algorithm is not public; the game only
    /// needs them non-zero and unique per model (two addon models sharing them get each
    /// other's bones when spawned together), so they are derived from the bone layout and
    /// salted with the model name.
    /// </summary>
    public static (uint u50, uint u54, uint u58) Signatures(string modelName, IReadOnlyList<GBone> bones)
    {
        var a = new StringBuilder(modelName);
        var b = new StringBuilder(modelName);
        for (int i = 0; i < bones.Count; i++)
        {
            var tag = i == 0 ? 0 : BoneTag(bones[i].Name);
            a.Append(' ').Append(tag).Append(':').Append(bones[i].Parent);
            b.Append(' ').Append(tag).Append(bones[i].Translation).Append(bones[i].Rotation);
        }
        uint u50 = JenkHash.GenHash(a.ToString());
        uint u54 = Crc32.HashToUInt32(Encoding.UTF8.GetBytes(a.ToString()));
        uint u58 = Crc32.HashToUInt32(Encoding.UTF8.GetBytes(b.ToString()));
        return (u50 == 0 ? 1 : u50, u54 == 0 ? 1 : u54, u58 == 0 ? 1 : u58);
    }
}
