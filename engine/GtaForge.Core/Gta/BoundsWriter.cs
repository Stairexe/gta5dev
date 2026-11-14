using System.Numerics;

namespace GtaForge.Core.Gta;

public enum CollisionMode { None, Box, Mesh }

/// <summary>Writes embedded drawable collision (&lt;Bounds type="Composite"&gt;) in CodeWalker XML.</summary>
public static class BoundsWriter
{
    // "General (Default)" collision preset used for ordinary props.
    const string TypeFlags = "MAP_WEAPON, MAP_DYNAMIC, MAP_ANIMAL, MAP_COVER, MAP_VEHICLE";
    const string IncludeFlags = "VEHICLE_NOT_BVH, VEHICLE_BVH, PED, RAGDOLL, ANIMAL, ANIMAL_RAGDOLL, OBJECT, PLANT, " +
                                "PROJECTILE, EXPLOSION, FORKLIFT_FORKS, TEST_WEAPON, TEST_CAMERA, TEST_AI, TEST_SCRIPT, " +
                                "TEST_VEHICLE_WHEEL, GLASS";

    public sealed record CollisionMesh(Vector3[] Positions, int[] Indices);

    public static string? Build(CollisionMode mode, int materialIndex, Aabb box, CollisionMesh? mesh)
    {
        if (mode == CollisionMode.None || box.IsEmpty) return null;
        if (mode == CollisionMode.Mesh && (mesh == null || mesh.Indices.Length < 3)) mode = CollisionMode.Box;

        var x = new Xw();
        x.Open("Bounds", "type=\"Composite\"");
        Common(x, box, 0, margin: 0.005f);
        x.Open("Children");
        if (mode == CollisionMode.Box) BoxChild(x, box, materialIndex);
        else MeshChild(x, mesh!, materialIndex);
        x.Close("Children");
        x.Close("Bounds");
        return x.ToString();
    }

    static void Common(Xw x, Aabb box, int material, float margin)
    {
        var size = box.Size;
        var volume = MathF.Max(size.X * size.Y * size.Z, 1e-4f);
        var inertia = new Vector3(
            (size.Y * size.Y + size.Z * size.Z) / 12f,
            (size.X * size.X + size.Z * size.Z) / 12f,
            (size.X * size.X + size.Y * size.Y) / 12f);
        x.Vec3("BoxMin", box.Min);
        x.Vec3("BoxMax", box.Max);
        x.Vec3("BoxCenter", box.Center);
        x.Vec3("SphereCenter", box.Center);
        x.Value("SphereRadius", box.Radius);
        x.Value("Margin", margin);
        x.Value("Volume", volume);
        x.Vec3("Inertia", inertia);
        x.Value("MaterialIndex", material);
        x.Value("MaterialColourIndex", 0);
        x.Value("ProceduralID", 0);
        x.Value("RoomID", 0);
        x.Value("PedDensity", 0);
        x.Value("UnkFlags", 0);
        x.Value("PolyFlags", 0);
        x.Value("UnkType", 1);
    }

    static void ChildTransform(Xw x)
    {
        x.Raw("CompositeTransform", ["1 0 0 0", "0 1 0 0", "0 0 1 0", "0 0 0 1"]);
        x.Text("CompositeFlags1", TypeFlags);
        x.Text("CompositeFlags2", IncludeFlags);
    }

    static void BoxChild(Xw x, Aabb box, int material)
    {
        x.Open("Item", "type=\"Box\"");
        Common(x, box, material, margin: 0.04f);
        ChildTransform(x);
        x.Close("Item");
    }

    static void MeshChild(Xw x, CollisionMesh mesh, int material)
    {
        var box = Aabb.Of(mesh.Positions);
        var center = box.Center;
        x.Open("Item", "type=\"GeometryBVH\"");
        Common(x, box, 0, margin: 0.005f);
        ChildTransform(x);
        x.Vec3("GeometryCenter", center);
        x.Value("UnkFloat1", 7.629627e-8f);
        x.Value("UnkFloat2", 0.0025f);
        x.Open("Materials");
        x.Open("Item");
        x.Value("Type", material);
        x.Value("ProceduralID", 0);
        x.Value("RoomID", 0);
        x.Value("PedDensity", 0);
        x.Text("Flags", "NONE");
        x.Value("MaterialColourIndex", 0);
        x.Value("Unk", 0);
        x.Close("Item");
        x.Close("Materials");
        x.Raw("Vertices", mesh.Positions.Select(p =>
        {
            var v = p - center;
            return $"{Xw.F(v.X)}, {Xw.F(v.Y)}, {Xw.F(v.Z)}";
        }));
        x.Raw("Polygons", Triangles(mesh));
        x.Close("Item");
    }

    static IEnumerable<string> Triangles(CollisionMesh mesh)
    {
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            if (a == b || b == c || a == c) continue;
            yield return $"<Triangle m=\"0\" v1=\"{a}\" v2=\"{b}\" v3=\"{c}\" f1=\"0\" f2=\"0\" f3=\"0\" />";
        }
    }

    /// <summary>Merges every mesh of a LOD into one object-space collision mesh (vertices welded at 1 mm).</summary>
    public static CollisionMesh FromLod(GLod lod, Matrix4x4[] boneGlobals)
    {
        var verts = new List<Vector3>();
        var idx = new List<int>();
        var weld = new Dictionary<(int, int, int), int>();
        foreach (var m in lod.Meshes)
        {
            var mtx = boneGlobals.Length > m.Bone ? boneGlobals[m.Bone] : Matrix4x4.Identity;
            var remap = new int[m.Positions.Length];
            for (int v = 0; v < m.Positions.Length; v++)
            {
                var p = Vector3.Transform(m.Positions[v], mtx);
                var key = ((int)MathF.Round(p.X * 1000), (int)MathF.Round(p.Y * 1000), (int)MathF.Round(p.Z * 1000));
                if (!weld.TryGetValue(key, out int id)) { id = verts.Count; verts.Add(p); weld[key] = id; }
                remap[v] = id;
            }
            foreach (var i in m.Indices) idx.Add(remap[i]);
        }
        return new CollisionMesh(verts.ToArray(), idx.ToArray());
    }
}
