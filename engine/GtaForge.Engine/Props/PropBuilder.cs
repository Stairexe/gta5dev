using System.Numerics;
using System.Text.RegularExpressions;
using GtaForge.Engine.Gta;
using GtaForge.Engine.Packaging;

namespace GtaForge.Engine.Props;

public sealed record BuildReport(List<string> Checks, List<string> Warnings);

public sealed record PropBuildResult(ResourceZip Resource, BuildReport Report);

/// <summary>Builds a streamable FiveM prop resource (.ydr, .ytd, .ytyp, optional .ycd) from a <see cref="PropRequest"/>.</summary>
public static partial class PropBuilder
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,60}$")]
    private static partial Regex NameRule();

    public static string NormalizeName(string name)
    {
        var n = TextureWriter.SafeName(name);
        if (n.Length == 0 || !char.IsAsciiLetter(n[0])) n = "prop_" + n;
        return n.Length > 60 ? n[..60] : n;
    }

    public static PropBuildResult Build(PropRequest req, IReadOnlyDictionary<string, byte[]> files)
    {
        var name = NormalizeName(req.Name);
        if (!NameRule().IsMatch(name)) throw new ArgumentException($"Invalid prop name '{name}'.");
        if (req.Lods.High.Count == 0) throw new ArgumentException("The model has no meshes.");

        var checks = new List<string>();
        var warnings = new List<string>();
        var work = Directory.CreateTempSubdirectory("gf_prop_").FullName;
        try
        {
            // ---- skeleton ----
            var bones = req.Bones.Count > 0 ? req.Bones.Select(b => b.ToBone()).ToList() : [new GBone { Name = "root" }];
            bones[0].Parent = -1;
            for (int i = 1; i < bones.Count; i++)
                if (bones[i].Parent < 0 || bones[i].Parent >= i)
                    throw new ArgumentException($"Bone '{bones[i].Name}' must come after its parent.");
            if (bones.Count > 128) throw new ArgumentException("A prop skeleton can have at most 128 bones.");
            var tags = bones.Skip(1).Select(b => SkeletonMath.BoneTag(b.Name)).ToList();
            if (tags.Distinct().Count() != tags.Count)
                throw new ArgumentException("Two bones produce the same bone tag; rename one of them.");

            // ---- geometry ----
            GLod ToLod(List<MeshRequest> meshes) => new() { Meshes = meshes.Select(m => m.ToMesh()).ToList() };
            var high = ToLod(req.Lods.High);
            var med = req.Lods.Medium is { Count: > 0 } m1 ? ToLod(m1) : null;
            var low = req.Lods.Low is { Count: > 0 } m2 ? ToLod(m2) : null;
            foreach (var mesh in high.Meshes.Concat(med?.Meshes ?? []).Concat(low?.Meshes ?? []))
            {
                if (mesh.Material < 0 || mesh.Material >= Math.Max(1, req.Materials.Count))
                    throw new ArgumentException("Mesh references a missing material.");
                if (mesh.Bone < 0 || mesh.Bone >= bones.Count)
                    throw new ArgumentException("Mesh references a missing bone.");
            }
            int tris = high.Meshes.Sum(m => m.Indices.Length / 3);
            if (tris > 150_000) warnings.Add($"High LOD has {tris:N0} triangles; props this dense stream slowly. Consider simplifying.");

            // ---- animations ----
            var anims = req.Animations
                .Where(a => a.FrameCount > 1 && a.Tracks.Count > 0)
                .Select(a => new GAnimation
                {
                    Name = TextureWriter.SafeName(a.Name),
                    FrameCount = a.FrameCount,
                    Duration = (a.FrameCount - 1) / Math.Max(1f, a.Fps),
                    Tracks = a.Tracks.Where(t => t.Bone >= 0 && t.Bone < bones.Count).Select(t => new GTrack
                    {
                        Bone = t.Bone,
                        Translations = t.T is { Length: > 0 } ? RequestMapping.V3(t.T) : null,
                        Rotations = t.R is { Length: > 0 } ? RequestMapping.Q(t.R) : null,
                    }).ToList(),
                }).ToList();
            if (anims.Select(a => a.Name).Distinct().Count() != anims.Count)
                throw new ArgumentException("Animation names must be unique.");
            bool animated = anims.Count > 0;
            if (animated && bones.Count < 2)
                warnings.Add("Animations only move the root bone; the whole prop will move as one piece.");

            // ---- bounds over rest pose + every animation frame ----
            var rest = SkeletonMath.Globals(bones);
            var box = Aabb.Empty;
            foreach (var mesh in high.Meshes) box = box.Include(MeshOps.BoundsIn(mesh, rest[mesh.Bone]));
            foreach (var a in anims)
                for (int f = 0; f < a.FrameCount; f++)
                {
                    var g = SkeletonMath.GlobalsAtFrame(bones, a, f);
                    foreach (var mesh in high.Meshes) box = box.Include(MeshOps.BoundsIn(mesh, g[mesh.Bone]));
                }
            if (box.Size.Length() < 0.01f) warnings.Add("The model is smaller than 1 cm. Check the export scale (GTA uses metres).");
            if (box.Size.Length() > 500f) warnings.Add("The model is over 500 m across. Check the export scale (GTA uses metres).");
            checks.Add($"Bounds {Fmt(box.Size)} m (covers the rest pose and all {anims.Sum(a => a.FrameCount)} animation frames)");

            // ---- textures + materials ----
            var texInputs = new List<TextureInput>();
            foreach (var t in req.Textures)
            {
                if (!files.TryGetValue(t.Field, out var bytes)) throw new ArgumentException($"Missing texture file '{t.Field}'.");
                var kind = t.Kind switch { "normal" => TextureKind.Normal, "specular" => TextureKind.Specular, _ => TextureKind.Diffuse };
                texInputs.Add(new TextureInput(TextureWriter.SafeName(t.Name), bytes, kind));
            }
            var materials = new List<GMaterial>();
            var reqMats = req.Materials.Count > 0 ? req.Materials : [new MaterialRequest()];
            for (int i = 0; i < reqMats.Count; i++)
            {
                var r = reqMats[i];
                string? diffuse = r.Diffuse is null ? null : TextureWriter.SafeName(r.Diffuse);
                if (diffuse == null)
                {
                    var c = r.Color is { Length: >= 3 } col ? col : [200, 200, 200, 255];
                    diffuse = $"{name}_m{i}_col";
                    texInputs.Add(new TextureInput(diffuse,
                        TextureWriter.SolidRgbaPng((byte)c[0], (byte)c[1], (byte)c[2], (byte)(c.Length > 3 ? c[3] : 255)),
                        TextureKind.Diffuse));
                }
                string? normal = r.Normal is null ? null : TextureWriter.SafeName(r.Normal);
                string? spec = r.Specular is null ? null : TextureWriter.SafeName(r.Specular);
                // Shader follows the maps that exist: a normal map without a spec map must not
                // reference a spec sampler, etc.
                var shader = r.Shader == "emissive" ? "emissive" : (normal, spec) switch
                {
                    (not null, not null) => "normal_spec",
                    (not null, null) => "normal",
                    (null, not null) => "spec",
                    _ => "default",
                };
                materials.Add(new GMaterial
                {
                    Name = r.Name,
                    Shader = shader,
                    Alpha = r.Alpha switch { "cutout" or "mask" => AlphaMode.Cutout, "blend" => AlphaMode.Blend, _ => AlphaMode.Opaque },
                    Diffuse = diffuse,
                    Normal = normal,
                    Specular = spec,
                    EmissiveMultiplier = r.EmissiveMultiplier,
                });
            }
            var missing = materials.SelectMany(m => new[] { m.Diffuse, m.Normal, m.Specular })
                .Where(n => n != null && texInputs.All(t => t.Name != n)).Distinct().ToList();
            if (missing.Count > 0) throw new ArgumentException("Materials reference missing textures: " + string.Join(", ", missing));

            // ---- collision ----
            var mode = req.Collision.Mode switch { "none" => CollisionMode.None, "mesh" => CollisionMode.Mesh, _ => CollisionMode.Box };
            BoundsWriter.CollisionMesh? colMesh = null;
            if (mode == CollisionMode.Mesh)
            {
                colMesh = req.Collision.Positions is { Length: >= 9 } cp && req.Collision.Indices is { Length: >= 3 } ci
                    ? new BoundsWriter.CollisionMesh(RequestMapping.V3(cp), ci)
                    : BoundsWriter.FromLod(low ?? med ?? high, rest);
                if (colMesh.Indices.Length / 3 > 20_000)
                    warnings.Add($"Collision mesh has {colMesh.Indices.Length / 3:N0} triangles. Use a simplified collision mesh or Box for better performance.");
            }
            var restBox = Aabb.Empty;
            foreach (var mesh in high.Meshes) restBox = restBox.Include(MeshOps.BoundsIn(mesh, rest[mesh.Bone]));
            var boundsXml = BoundsWriter.Build(mode, Math.Clamp(req.Collision.Material, 0, 255), restBox, colMesh);

            // ---- drawable ----
            var ydrXml = new DrawableWriter
            {
                Name = name,
                Materials = materials,
                Bones = bones,
                High = high,
                Medium = med,
                Low = low,
                // Switch distances: High->Medium, Medium->Low; the last LOD lasts until lodDist.
                LodDistHigh = med != null ? MedSwitch(req) : req.LodDist,
                LodDistMedium = low != null ? LowSwitch(req) : req.LodDist,
                LodDistLow = req.LodDist,
                Bounds = box,
                BoundsXml = boundsXml,
                WriteSkeleton = animated || bones.Count > 1,
            }.Build();
            var ydr = GtaIO.YdrFromXml(ydrXml, work);
            var ytd = TextureWriter.BuildYtd(texInputs, Path.Combine(work, "tex"));
            byte[]? ycd = animated ? YcdWriter.Build(bones, anims) : null;

            var flags = ArchetypeFlags.UseAmbientScale;
            if (animated) flags |= ArchetypeFlags.HasAnim | ArchetypeFlags.AutoStartAnim;
            if (req.Dynamic) flags |= ArchetypeFlags.Dynamic;
            var ytyp = YtypWriter.Build(name, [new ArchetypeSpec
            {
                Name = name,
                TextureDictionary = name,
                ClipDictionary = animated ? name : null,
                HasPhysics = mode != CollisionMode.None,
                Bounds = box,
                LodDist = req.LodDist,
                HdTextureDist = Math.Min(60f, req.LodDist),
                Flags = flags,
            }]);

            // ---- read everything back (do not trust a write we have not re-read) ----
            Verify(name, ydr, ytd, ytyp, ycd, high, materials.Count, texInputs.Count, anims, mode, checks);

            var res = new ResourceZip(name);
            res.Add($"stream/{name}.ydr", ydr);
            res.Add($"stream/{name}.ytd", ytd);
            res.Add($"stream/{name}.ytyp", ytyp);
            if (ycd != null) res.Add($"stream/{name}.ycd", ycd);
            res.Add("fxmanifest.lua", Manifest(name, req.TestCommand));
            if (req.TestCommand) res.Add("client/test_spawn.lua", TestScript(name, anims));
            res.Add("README.md", Readme(name, anims, mode, req));
            return new PropBuildResult(res, new BuildReport(checks, warnings));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp cleanup */ }
        }
    }

    static void Verify(string name, byte[] ydrBytes, byte[] ytdBytes, byte[] ytypBytes, byte[]? ycdBytes,
                       GLod high, int materialCount, int textureCount, List<GAnimation> anims, CollisionMode mode, List<string> checks)
    {
        var ydr = GtaIO.LoadYdr(ydrBytes, name + ".ydr");
        var d = ydr.Drawable ?? throw new GtaFormatException("Built YDR could not be read back.");
        int verts = d.DrawableModels.High.Sum(m => m.Geometries.Sum(g => (int)g.VerticesCount));
        int expected = high.Meshes.Sum(m => m.Positions.Length);
        if (verts != expected) throw new GtaFormatException($"YDR read-back has {verts} vertices, expected {expected}.");
        if ((d.ShaderGroup?.Shaders?.data_items?.Length ?? 0) != materialCount)
            throw new GtaFormatException("YDR read-back shader count does not match the materials.");
        if (mode != CollisionMode.None && d.Bound == null) throw new GtaFormatException("YDR read-back has no collision.");
        checks.Add($"YDR: {d.DrawableModels.High.Length} model(s), {verts:N0} vertices, {materialCount} shader(s), " +
                   $"collision {(d.Bound == null ? "none" : d.Bound.Type.ToString())}" +
                   (d.Skeleton != null ? $", skeleton {d.Skeleton.Bones.Items.Length} bones" : ""));

        var ytd = GtaIO.LoadYtd(ytdBytes, name + ".ytd");
        int texCount = ytd.TextureDict?.Textures?.data_items?.Length ?? 0;
        if (texCount != textureCount) throw new GtaFormatException($"YTD read-back has {texCount} textures, expected {textureCount}.");
        checks.Add($"YTD: {texCount} texture(s) " + string.Join(", ",
            ytd.TextureDict!.Textures.data_items.Select(t => $"{t.Name} {t.Width}x{t.Height} {t.Format}")));

        var ytyp = GtaIO.LoadYtyp(ytypBytes, name + ".ytyp");
        var arch = ytyp.AllArchetypes?.FirstOrDefault() ?? throw new GtaFormatException("YTYP read-back has no archetype.");
        if (arch.Hash != CodeWalker.GameFiles.JenkHash.GenHash(name)) throw new GtaFormatException("YTYP archetype name mismatch.");
        checks.Add($"YTYP: archetype '{name}', flags {arch._BaseArchetypeDef.flags}, lodDist {arch._BaseArchetypeDef.lodDist}");

        if (ycdBytes != null)
        {
            var ycd = GtaIO.LoadYcd(ycdBytes, name + ".ycd");
            var cd = ycd.ClipDictionary ?? throw new GtaFormatException("YCD read-back failed.");
            int clips = ycd.ClipMap?.Count ?? 0;
            int animsCount = ycd.AnimMap?.Count ?? 0;
            // AnimMap == 0 is the classic silent failure: PlayEntityAnim returns true but nothing moves.
            if (clips != anims.Count || animsCount != anims.Count)
                throw new GtaFormatException($"YCD read-back: ClipMap={clips}, AnimMap={animsCount}, expected {anims.Count}.");
            foreach (var a in anims)
                if (!ycd.ClipMap!.ContainsKey(CodeWalker.GameFiles.JenkHash.GenHash(a.Name)))
                    throw new GtaFormatException($"YCD read-back is missing clip '{a.Name}'.");
            checks.Add($"YCD: {clips} clip(s) / {animsCount} animation(s): " + string.Join(", ", anims.Select(a => $"{a.Name} ({a.Duration:0.##}s)")));
        }
    }

    static float MedSwitch(PropRequest r) => r.LodDistMedium > 0 && r.LodDistMedium < r.LodDist ? r.LodDistMedium : r.LodDist * 0.35f;
    static float LowSwitch(PropRequest r) => r.LodDistLow > 0 && r.LodDistLow < r.LodDist ? Math.Max(r.LodDistLow, MedSwitch(r)) : r.LodDist * 0.7f;

    static string Fmt(Vector3 v) => $"{v.X:0.##} x {v.Y:0.##} x {v.Z:0.##}";

    static string Manifest(string name, bool test)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine("lua54 'yes'");
        sb.AppendLine();
        sb.AppendLine($"description '{name} - generated by GTA Forge'");
        sb.AppendLine();
        sb.AppendLine("files {");
        sb.AppendLine($"    'stream/{name}.ytyp',");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"data_file 'DLC_ITYP_REQUEST' 'stream/{name}.ytyp'");
        if (test)
        {
            sb.AppendLine();
            sb.AppendLine("client_script 'client/test_spawn.lua'");
        }
        return sb.ToString();
    }

    static string TestScript(string name, List<GAnimation> anims)
    {
        var clip = anims.FirstOrDefault()?.Name;
        var animLines = clip == null ? "" :
            $$"""

                RequestAnimDict('{{name}}')
                local t = GetGameTimer()
                while not HasAnimDictLoaded('{{name}}') and GetGameTimer() - t < 5000 do Wait(0) end
                PlayEntityAnim(obj, '{{clip}}', '{{name}}', 1000.0, true, true, false, 0.0, 0)
            """;
        return $$"""
            -- Test command: /{{name}} spawns the prop 2 m in front of you. /{{name}}_del removes it.
            local spawned = {}

            RegisterCommand('{{name}}', function()
                local model = `{{name}}`
                if not IsModelInCdimage(model) then
                    print('^1{{name}}: model not found. Is the resource started (ensure {{name}})?^0')
                    return
                end
                RequestModel(model)
                local t = GetGameTimer()
                while not HasModelLoaded(model) and GetGameTimer() - t < 5000 do Wait(0) end
                local ped = PlayerPedId()
                local pos = GetOffsetFromEntityInWorldCoords(ped, 0.0, 2.0, 0.0)
                local obj = CreateObject(model, pos.x, pos.y, pos.z, false, false, false)
                PlaceObjectOnGroundProperly(obj)
                SetEntityHeading(obj, GetEntityHeading(ped))
                FreezeEntityPosition(obj, {{(anims.Count > 0 ? "false" : "true")}})
                SetModelAsNoLongerNeeded(model){{animLines}}
                spawned[#spawned + 1] = obj
            end, false)

            RegisterCommand('{{name}}_del', function()
                for _, obj in ipairs(spawned) do
                    if DoesEntityExist(obj) then DeleteObject(obj) end
                end
                spawned = {}
            end, false)
            """;
    }

    static string Readme(string name, List<GAnimation> anims, CollisionMode mode, PropRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {name}").AppendLine().AppendLine("Generated by GTA Forge.").AppendLine();
        sb.AppendLine("## Install (FiveM)");
        sb.AppendLine($"1. Copy the `{name}` folder into your server's `resources` folder.");
        sb.AppendLine($"2. Add `ensure {name}` to `server.cfg`.");
        sb.AppendLine("3. Restart the server and join.");
        if (req.TestCommand)
            sb.AppendLine($"4. Type `/{name}` in chat to spawn it in front of you, `/{name}_del` to remove it.");
        sb.AppendLine().AppendLine("## Files").AppendLine("| file | what it is |").AppendLine("|---|---|");
        sb.AppendLine($"| `stream/{name}.ydr` | the model ({(req.Lods.Medium is { Count: > 0 } ? "with LODs, " : "")}collision: {mode.ToString().ToLowerInvariant()}) |");
        sb.AppendLine($"| `stream/{name}.ytd` | its textures |");
        sb.AppendLine($"| `stream/{name}.ytyp` | the archetype: makes `{name}` a spawnable/placeable object |");
        if (anims.Count > 0)
            sb.AppendLine($"| `stream/{name}.ycd` | animation clips: {string.Join(", ", anims.Select(a => $"`{a.Name}`"))} |");
        sb.AppendLine().AppendLine("## Use it in your scripts").AppendLine("```lua");
        sb.AppendLine($"local obj = CreateObject(`{name}`, x, y, z, true, true, false)").AppendLine("```");
        if (anims.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("The archetype has the *Has Anim* and *Auto Start Anim* flags, so copies placed in a ymap or MLO");
            sb.AppendLine($"play `{anims[0].Name}` by themselves. From a script:");
            sb.AppendLine("```lua");
            sb.AppendLine($"RequestAnimDict('{name}')");
            sb.AppendLine($"while not HasAnimDictLoaded('{name}') do Wait(0) end");
            sb.AppendLine($"PlayEntityAnim(obj, '{anims[0].Name}', '{name}', 1000.0, true, true, false, 0.0, 0)");
            sb.AppendLine("```");
            sb.AppendLine("A frozen object will not play a clip that moves its root bone; unfreeze it first.");
        }
        return sb.ToString();
    }
}
