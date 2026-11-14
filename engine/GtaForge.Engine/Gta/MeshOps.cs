using System.Numerics;

namespace GtaForge.Engine.Gta;

public static class MeshOps
{
    /// <summary>Per-vertex tangents (xyz + handedness in w) from positions, normals and UV0.</summary>
    public static Vector4[] Tangents(GMesh m)
    {
        int n = m.Positions.Length;
        var tan = new Vector3[n];
        var bit = new Vector3[n];
        for (int i = 0; i + 2 < m.Indices.Length; i += 3)
        {
            int a = m.Indices[i], b = m.Indices[i + 1], c = m.Indices[i + 2];
            if (m.Uv0.Length <= Math.Max(a, Math.Max(b, c))) continue;
            var p1 = m.Positions[b] - m.Positions[a];
            var p2 = m.Positions[c] - m.Positions[a];
            var t1 = m.Uv0[b] - m.Uv0[a];
            var t2 = m.Uv0[c] - m.Uv0[a];
            float d = t1.X * t2.Y - t2.X * t1.Y;
            if (MathF.Abs(d) < 1e-12f) continue;
            float r = 1f / d;
            var sdir = (p1 * t2.Y - p2 * t1.Y) * r;
            var tdir = (p2 * t1.X - p1 * t2.X) * r;
            tan[a] += sdir; tan[b] += sdir; tan[c] += sdir;
            bit[a] += tdir; bit[b] += tdir; bit[c] += tdir;
        }
        var result = new Vector4[n];
        for (int v = 0; v < n; v++)
        {
            var nrm = m.Normals.Length > v ? m.Normals[v] : Vector3.UnitZ;
            var t = tan[v] - nrm * Vector3.Dot(nrm, tan[v]);
            if (t.LengthSquared() < 1e-12f)
                t = MathF.Abs(nrm.X) < 0.9f ? Vector3.Cross(nrm, Vector3.UnitX) : Vector3.Cross(nrm, Vector3.UnitY);
            t = Vector3.Normalize(t);
            float w = Vector3.Dot(Vector3.Cross(nrm, t), bit[v]) < 0 ? -1f : 1f;
            result[v] = new Vector4(t, w);
        }
        return result;
    }

    /// <summary>Builds a new mesh from a vertex remap (old index -> new index) and new index list.</summary>
    public static GMesh Remap(GMesh m, Dictionary<int, int> map, int[] indices)
    {
        int n = map.Count;
        var order = new int[n];
        foreach (var (src, dst) in map) order[dst] = src;

        T[] Pick<T>(T[] arr) => order.Select(i => arr[i]).ToArray();
        T[]? PickOpt<T>(T[]? arr) => arr == null || arr.Length == 0 ? arr : Pick(arr);
        byte[]? Pick4(byte[]? arr)
        {
            if (arr == null) return null;
            var r = new byte[n * 4];
            for (int i = 0; i < n; i++) Array.Copy(arr, order[i] * 4, r, i * 4, 4);
            return r;
        }

        return new GMesh
        {
            Material = m.Material,
            Bone = m.Bone,
            Positions = Pick(m.Positions),
            Normals = PickOpt(m.Normals) ?? [],
            Uv0 = PickOpt(m.Uv0) ?? [],
            Uv1 = PickOpt(m.Uv1),
            Colors0 = Pick4(m.Colors0),
            Colors1 = Pick4(m.Colors1),
            BlendIndices = Pick4(m.BlendIndices),
            BlendWeights = Pick4(m.BlendWeights),
            Indices = indices,
        };
    }

    public static Vector3 Transform(Vector3 p, Matrix4x4 m) => Vector3.Transform(p, m);

    /// <summary>Object-space AABB of a mesh given the transform of the bone it is attached to.</summary>
    public static Aabb BoundsIn(GMesh m, Matrix4x4 boneToObject)
    {
        var local = Aabb.Of(m.Positions);
        if (local.IsEmpty) return local;
        var r = Aabb.Empty;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? local.Min.X : local.Max.X,
                                (i & 2) == 0 ? local.Min.Y : local.Max.Y,
                                (i & 4) == 0 ? local.Min.Z : local.Max.Z);
            r = r.Include(Vector3.Transform(c, boneToObject));
        }
        return r;
    }
}
