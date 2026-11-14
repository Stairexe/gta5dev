using System.Numerics;

namespace GtaForge.Core.Gta;

/// <summary>
/// Writes a clip dictionary (.ycd) for prop skeleton animations: one clip per animation,
/// tracks 0 (bone translation) and 1 (bone rotation) keyed by bone tag.
/// </summary>
public static class YcdWriter
{
    // Track ids in the RAGE animation system.
    const int TrackBoneTranslation = 0;
    const int TrackBoneRotation = 1;

    public static string BuildXml(IReadOnlyList<GBone> bones, IEnumerable<GAnimation> animations)
    {
        var anims = animations.Where(a => a.FrameCount > 0).ToList();
        var x = new Xw();
        x.Open("ClipDictionary");
        x.Open("Clips");
        foreach (var a in anims)
        {
            x.Open("Item");
            x.Text("Hash", a.Name);
            x.Text("Name", "pack:/" + a.Name);
            x.Value("Type", "Animation");
            x.Value("Unknown30", 0);
            x.Empty("Tags");
            x.Empty("Properties");
            x.Text("AnimationHash", a.Name);
            x.Value("StartTime", 0f);
            x.Value("EndTime", a.Duration);
            x.Value("Rate", 1f);
            x.Close("Item");
        }
        x.Close("Clips");

        x.Open("Animations");
        foreach (var a in anims)
        {
            // (boneTag, track, channel values)
            var entries = new List<(ushort tag, int track, Action<Xw> channels)>();
            foreach (var t in a.Tracks.OrderBy(t => t.Bone))
            {
                ushort tag = t.Bone == 0 ? (ushort)0 : SkeletonMath.BoneTag(bones[t.Bone].Name);
                if (t.Translations is { Length: > 0 } ts)
                    entries.Add((tag, TrackBoneTranslation, w => Vec3Channels(w, Pad(ts, a.FrameCount))));
                if (t.Rotations is { Length: > 0 } rs)
                    entries.Add((tag, TrackBoneRotation, w => QuatChannels(w, Pad(Continuous(rs), a.FrameCount))));
            }
            // Same order the game's exporter uses: key = boneId | (track << 16).
            entries = entries.OrderBy(e => e.track).ThenBy(e => e.tag).ToList();

            x.Open("Item");
            x.Text("Hash", a.Name);
            x.Value("Unknown10", 0);
            x.Value("FrameCount", a.FrameCount);
            x.Value("SequenceFrameLimit", a.FrameCount + 1);
            x.Value("Duration", a.Duration);
            x.Text("Unknown1C", "hash_22E95D79");
            x.Open("BoneIds");
            foreach (var e in entries)
            {
                x.Open("Item");
                x.Value("BoneId", e.tag);
                x.Value("Track", e.track);
                x.Value("Unk0", 0);
                x.Close("Item");
            }
            x.Close("BoneIds");
            x.Open("Sequences");
            x.Open("Item");
            x.Text("Hash", "hash_" + CodeWalker.GameFiles.JenkHash.GenHash(a.Name + "_seq").ToString("X8"));
            x.Value("FrameCount", a.FrameCount);
            x.Open("SequenceData");
            foreach (var e in entries)
            {
                x.Open("Item");
                x.Open("Channels");
                e.channels(x);
                x.Close("Channels");
                x.Close("Item");
            }
            x.Close("SequenceData");
            x.Close("Item");
            x.Close("Sequences");
            x.Close("Item");
        }
        x.Close("Animations");
        x.Close("ClipDictionary");
        return x.ToString();
    }

    public static byte[] Build(IReadOnlyList<GBone> bones, IEnumerable<GAnimation> animations) =>
        GtaIO.YcdFromXml(BuildXml(bones, animations));

    static T[] Pad<T>(T[] v, int n)
    {
        if (v.Length == n) return v;
        var r = new T[n];
        for (int i = 0; i < n; i++) r[i] = v[Math.Min(i, v.Length - 1)];
        return r;
    }

    /// <summary>Keeps neighbouring quaternions in the same hemisphere so interpolation takes the short way.</summary>
    static Quaternion[] Continuous(Quaternion[] q)
    {
        var r = (Quaternion[])q.Clone();
        for (int i = 1; i < r.Length; i++)
            if (Quaternion.Dot(r[i - 1], r[i]) < 0) r[i] = Quaternion.Negate(r[i]);
        return r;
    }

    static void Vec3Channels(Xw x, Vector3[] v)
    {
        if (v.All(p => Vector3.DistanceSquared(p, v[0]) < 1e-12f))
        {
            x.Open("Item");
            x.Value("Type", "StaticVector3");
            x.Vec3("Value", v[0]);
            x.Close("Item");
            return;
        }
        FloatChannel(x, v.Select(p => p.X).ToArray());
        FloatChannel(x, v.Select(p => p.Y).ToArray());
        FloatChannel(x, v.Select(p => p.Z).ToArray());
    }

    static void QuatChannels(Xw x, Quaternion[] q)
    {
        if (q.All(p => MathF.Abs(Quaternion.Dot(p, q[0])) > 1 - 1e-7f))
        {
            x.Open("Item");
            x.Value("Type", "StaticQuaternion");
            x.Quat("Value", q[0]);
            x.Close("Item");
            return;
        }
        FloatChannel(x, q.Select(p => p.X).ToArray());
        FloatChannel(x, q.Select(p => p.Y).ToArray());
        FloatChannel(x, q.Select(p => p.Z).ToArray());
        FloatChannel(x, q.Select(p => p.W).ToArray());
    }

    static void FloatChannel(Xw x, float[] values)
    {
        float min = values.Min(), max = values.Max();
        x.Open("Item");
        if (max - min < 1e-7f)
        {
            x.Value("Type", "StaticFloat");
            x.Value("Value", values[0]);
        }
        else
        {
            // 16-bit quantisation over the channel's range.
            float quantum = (max - min) / 65535f;
            x.Value("Type", "QuantizeFloat");
            x.Value("Quantum", quantum);
            x.Value("Offset", min);
            x.Raw("Values", values.Chunk(10).Select(c => string.Join(' ', c.Select(Xw.F))));
        }
        x.Close("Item");
    }
}
