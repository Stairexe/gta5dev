using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using GtaForge.Core.Gta;
using GtaForge.Core.Packaging;
using GtaForge.Core.Props;

namespace GtaForge.Core.Peds;

public sealed record PedBuildResult(ResourceZip Resource, BuildReport Report);

/// <summary>
/// YDD -> addon ped. Takes a ped .ydd rigged to a GTA ped skeleton and produces
/// &lt;name&gt;.ydd (component drawables), .ytd (convention-named textures), .yft (skeleton),
/// .ymt (variation info), peds.meta and an fxmanifest.
/// </summary>
public static partial class PedBuilder
{
    [GeneratedRegex(@"^(head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_(\d{3})_([ur])$")]
    private static partial Regex ComponentName();

    static readonly Dictionary<uint, string> KnownComponentHashes = BuildKnown();

    static Dictionary<uint, string> BuildKnown()
    {
        var d = new Dictionary<uint, string>();
        foreach (var c in PedComponents.All)
            for (int i = 0; i < 64; i++)
                foreach (var r in "ur")
                {
                    var n = PedComponents.DrawableName(c, i, r);
                    d[JenkHash.GenHash(n)] = n;
                }
        return d;
    }

    sealed record OutDrawable(string Name, PedComponents.Comp Comp, int Index, XmlElement Xml, string? MainDiffuse, string? MainNormal, string? MainSpec);

    public static PedBuildResult Build(PedRequest req, PedInput input)
    {
        var name = PropBuilder.NormalizeName(req.Name);
        var checks = new List<string>();
        var warnings = new List<string>();
        var work = Directory.CreateTempSubdirectory("gf_ped_").FullName;
        try
        {
            var ydd = GtaIO.LoadYdd(input.Ydd, name + ".ydd");
            if (ydd.Drawables == null || ydd.Drawables.Length == 0) throw new ArgumentException("The .ydd contains no drawables.");

            // Name drawables whose names were stored only as hashes.
            for (int i = 0; i < ydd.Drawables.Length; i++)
            {
                var h = ydd.DrawableDict?.Hashes is { } hs && i < hs.Length ? hs[i] : 0u;
                if (KnownComponentHashes.TryGetValue(h, out var known)) ydd.Drawables[i].Name = known;
                else if (string.IsNullOrEmpty(ydd.Drawables[i].Name)) ydd.Drawables[i].Name = $"drawable_{i}";
            }

            var srcDds = Path.Combine(work, "src");
            Directory.CreateDirectory(srcDds);
            var doc = new XmlDocument();
            doc.LoadXml(YddXml.GetXml(ydd, srcDds));
            var items = doc.DocumentElement!.SelectNodes("Item")!.OfType<XmlElement>().ToList();

            // ---- skeleton ----
            var skeleton = items.Select(i => i.SelectSingleNode("Skeleton") as XmlElement).FirstOrDefault(s => s?.SelectSingleNode("Bones/Item") != null);
            YftFile? srcYft = input.Yft != null ? GtaIO.LoadYft(input.Yft, name + ".yft") : null;
            if (skeleton == null && srcYft?.Fragment?.Drawable?.Skeleton != null)
            {
                var yftDoc = new XmlDocument();
                yftDoc.LoadXml(YftXml.GetXml(srcYft, Path.Combine(work, "yft")));
                skeleton = (XmlElement?)yftDoc.SelectSingleNode("Fragment/Drawable/Skeleton");
                if (skeleton != null) skeleton = (XmlElement)doc.ImportNode(skeleton, true);
            }
            if (skeleton == null)
                throw new ArgumentException("No skeleton found. Export the .ydd with its skeleton (Sollumz: 'Exclude Skeleton' off) or also upload the ped's .yft.");
            var boneNames = skeleton.SelectNodes("Bones/Item")!.OfType<XmlElement>()
                .Select(b => b.SelectSingleNode("Name")?.InnerText ?? "").ToList();
            checks.Add($"Skeleton: {boneNames.Count} bones" + (boneNames.Count is 98 or 128 ? $" (standard {(boneNames.Count == 128 ? "freemode" : "ped")} count)" : ""));
            if (boneNames.Count is not (98 or 128))
                warnings.Add($"The skeleton has {boneNames.Count} bones; GTA ped skeletons have 98 (ambient peds) or 128 (freemode). Animations may not line up.");

            var gb = boneNames.Select((n, i) => new GBone { Name = n }).ToList();
            var (u50, u54, u58) = SkeletonMath.Signatures(name, gb);
            SetValue(skeleton, "Unknown50", u50);
            SetValue(skeleton, "Unknown54", u54);
            SetValue(skeleton, "Unknown58", u58);

            // ---- textures available from the .ytd and embedded in the .ydd ----
            var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(srcDds, "*.dds"))
                textures[Path.GetFileNameWithoutExtension(f)] = File.ReadAllBytes(f);
            if (input.Ytd != null)
            {
                var ytd = GtaIO.LoadYtd(input.Ytd, name + ".ytd");
                foreach (var t in ytd.TextureDict?.Textures?.data_items ?? [])
                    textures[t.Name] = DDSIO.GetDDSFile(t);
            }
            checks.Add($"Textures found: {textures.Count}" + (textures.Count > 0 ? $" ({string.Join(", ", textures.Keys.Take(8))}{(textures.Count > 8 ? ", ..." : "")})" : ""));

            // ---- drawables ----
            var named = items.Select(i => i.SelectSingleNode("Name")?.InnerText ?? "").ToList();
            bool alreadySplit = req.SplitMode != "split" && named.All(n => ComponentName().IsMatch(n));
            var outDrawables = alreadySplit
                ? KeepComponents(items, named, skeleton, doc)
                : SplitBody(items, skeleton, boneNames, doc, warnings);
            if (outDrawables.Count == 0) throw new ArgumentException("No skinned geometry was found in the .ydd.");
            checks.Add(alreadySplit
                ? $"Kept {outDrawables.Count} component drawable(s) as named"
                : $"Split into {outDrawables.Count} component(s): {string.Join(", ", outDrawables.Select(d => d.Name))}");

            // ---- convention-named textures for the variation system ----
            var outTexDir = Path.Combine(work, "out_tex");
            Directory.CreateDirectory(outTexDir);
            var ytdNames = new List<string>();
            void Emit(string texName, string? source, bool required)
            {
                if (ytdNames.Contains(texName)) return;
                byte[]? dds = source != null && textures.TryGetValue(source, out var b) ? b : null;
                if (dds == null)
                {
                    if (!required) return;
                    var png = TextureWriter.SolidRgbaPng(180, 180, 180);
                    var enc = TextureWriter.Encode(new TextureInput(texName, png, TextureKind.Diffuse), outTexDir);
                    File.Move(Path.Combine(outTexDir, enc.DdsFile), Path.Combine(outTexDir, texName + ".dds"), true);
                    warnings.Add($"No texture '{source ?? "(none)"}' was found for {texName}; a grey placeholder was used. Upload the ped's .ytd.");
                }
                else File.WriteAllBytes(Path.Combine(outTexDir, texName + ".dds"), dds);
                ytdNames.Add(texName);
            }
            foreach (var d in outDrawables)
            {
                Emit(PedComponents.DiffuseName(d.Comp, d.Index), d.MainDiffuse, required: true);
                if (d.MainNormal != null) Emit(PedComponents.NormalName(d.Comp, d.Index), d.MainNormal, required: false);
                if (d.MainSpec != null) Emit(PedComponents.SpecName(d.Comp, d.Index), d.MainSpec, required: false);
                // Secondary shaders keep their own texture names.
                foreach (XmlElement p in d.Xml.SelectNodes("ShaderGroup/Shaders/Item/Parameters/Item[@type='Texture']")!)
                {
                    var tn = p.SelectSingleNode("Name")?.InnerText;
                    if (!string.IsNullOrEmpty(tn) && textures.ContainsKey(tn)) Emit(tn, tn, required: false);
                }
            }

            // ---- assemble the .ydd (entries sorted by name hash: the game binary-searches them) ----
            var yddDoc = new XmlDocument();
            var root = yddDoc.AppendChild(yddDoc.CreateElement("DrawableDictionary"))!;
            foreach (var d in outDrawables.OrderBy(d => JenkHash.GenHash(d.Name)))
                root.AppendChild(yddDoc.ImportNode(d.Xml, true));
            var yddBytes = GtaIO.YddFromXml(yddDoc.OuterXml, outTexDir);

            var ytdBytes = BuildYtdFromDds(outTexDir, ytdNames);
            var ymtBytes = PedYmtWriter.Build(outDrawables.Select(d => new PedDrawable(d.Comp, d.Index, 1)).ToList());
            var yftBytes = BuildYft(name, srcYft, skeleton, (u50, u54, u58), work, checks, warnings);

            // ---- read back ----
            var yddBack = GtaIO.LoadYdd(yddBytes, name + ".ydd");
            var backNames = yddBack.DrawableDict.Hashes.Select(h => KnownComponentHashes.GetValueOrDefault(h, h.ToString("X8"))).ToList();
            if (yddBack.Drawables.Length != outDrawables.Count) throw new GtaFormatException("YDD read-back drawable count mismatch.");
            checks.Add($"YDD: {string.Join(", ", backNames)}");
            var ytdBack = GtaIO.LoadYtd(ytdBytes, name + ".ytd");
            checks.Add($"YTD: {string.Join(", ", ytdBack.TextureDict.Textures.data_items.Select(t => t.Name))}");
            var ymtBack = GtaIO.LoadYmt(ymtBytes, name + ".ymt");
            if (ymtBack.Pso == null) throw new GtaFormatException("YMT read-back failed (not a PSO file).");
            checks.Add($"YMT: PSO CPedVariationInfo with {outDrawables.Count} drawable(s)");

            var res = new ResourceZip(name);
            res.Add($"stream/{name}.ydd", yddBytes);
            res.Add($"stream/{name}.ytd", ytdBytes);
            res.Add($"stream/{name}.yft", yftBytes);
            res.Add($"stream/{name}.ymt", ymtBytes);
            res.Add("peds.meta", PedsMetaWriter.Build(name, req));
            res.Add("fxmanifest.lua", Manifest(name, req.TestCommand));
            if (req.TestCommand) res.Add("client/test_ped.lua", TestScript(name));
            res.Add("README.md", Readme(name, outDrawables, req));
            return new PedBuildResult(res, new BuildReport(checks, warnings));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp cleanup */ }
        }
    }

    // ---------- drawables ----------

    static List<OutDrawable> KeepComponents(List<XmlElement> items, List<string> names, XmlElement skeleton, XmlDocument doc)
    {
        var list = new List<OutDrawable>();
        for (int i = 0; i < items.Count; i++)
        {
            var m = ComponentName().Match(names[i]);
            var comp = PedComponents.ByShort(m.Groups[1].Value);
            int idx = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var x = (XmlElement)items[i].CloneNode(true);
            if (x.SelectSingleNode("ShaderGroup/TextureDictionary") is XmlNode td) td.ParentNode!.RemoveChild(td);
            ReplaceSkeleton(x, skeleton);
            var (dif, nrm, spec) = RenameMainTextures(x, MainShader(x), comp, idx);
            list.Add(new OutDrawable(names[i], comp, idx, x, dif, nrm, spec));
        }
        return list;
    }

    static List<OutDrawable> SplitBody(List<XmlElement> items, XmlElement skeleton, List<string> boneNames, XmlDocument doc, List<string> warnings)
    {
        // Merge every drawable's shaders into one list (geometry shader indices offset accordingly).
        var shaders = new List<XmlElement>();
        var pieces = new Dictionary<string, YddSplitter.Piece>();
        XmlElement? template = null;
        foreach (var item in items)
        {
            template ??= item;
            int offset = shaders.Count;
            shaders.AddRange(item.SelectNodes("ShaderGroup/Shaders/Item")!.OfType<XmlElement>());
            if (offset > 0)
                foreach (XmlElement si in item.SelectNodes(".//Geometries/Item/ShaderIndex")!)
                    si.SetAttribute("value", (int.Parse(si.GetAttribute("value"), CultureInfo.InvariantCulture) + offset).ToString(CultureInfo.InvariantCulture));
            foreach (var (comp, p) in YddSplitter.Split(item, boneNames, doc))
            {
                if (!pieces.TryGetValue(comp, out var acc)) { pieces[comp] = p; continue; }
                foreach (var (lod, geoms) in p.Geometries)
                {
                    if (!acc.Geometries.TryGetValue(lod, out var l)) acc.Geometries[lod] = l = [];
                    l.AddRange(geoms);
                }
                acc.Triangles += p.Triangles;
                foreach (var (s, n) in p.ShaderUse) acc.ShaderUse[s] = acc.ShaderUse.GetValueOrDefault(s) + n;
                acc.Min = Vector3.Min(acc.Min, p.Min); acc.Max = Vector3.Max(acc.Max, p.Max);
            }
        }

        var order = new[] { "head", "uppr", "lowr", "feet" };
        var list = new List<OutDrawable>();
        foreach (var compName in order)
        {
            if (!pieces.TryGetValue(compName, out var piece) || piece.Triangles == 0) continue;
            var comp = PedComponents.ByShort(compName);
            var dname = PedComponents.DrawableName(comp, 0, 'u');
            var x = (XmlElement)template!.CloneNode(false);
            AddText(doc, x, "Name", dname);
            var center = (piece.Min + piece.Max) * 0.5f;
            AddVec(doc, x, "BoundingSphereCenter", center);
            AddValue(doc, x, "BoundingSphereRadius", ((piece.Max - piece.Min).Length() * 0.5f).ToString(CultureInfo.InvariantCulture));
            AddVec(doc, x, "BoundingBoxMin", piece.Min);
            AddVec(doc, x, "BoundingBoxMax", piece.Max);
            foreach (var tag in new[] { "LodDistHigh", "LodDistMed", "LodDistLow", "LodDistVlow", "FlagsHigh", "FlagsMed", "FlagsLow", "FlagsVlow", "Unknown9A" })
                if (template.SelectSingleNode(tag) is XmlNode n) x.AppendChild(n.CloneNode(true));

            // Shader group with only the shaders this piece uses.
            var used = piece.Geometries.Values.SelectMany(g => g)
                .Select(g => int.Parse(((XmlElement)g.SelectSingleNode("ShaderIndex")!).GetAttribute("value"), CultureInfo.InvariantCulture))
                .Distinct().OrderBy(i => i).ToList();
            var remap = used.Select((s, i) => (s, i)).ToDictionary(t => t.s, t => t.i);
            var sg = doc.CreateElement("ShaderGroup");
            AddValue(doc, sg, "Unknown30", "8");
            var sh = doc.CreateElement("Shaders");
            foreach (var s in used) sh.AppendChild(shaders[s].CloneNode(true));
            sg.AppendChild(sh);
            x.AppendChild(sg);
            x.AppendChild(skeleton.CloneNode(true));

            foreach (var lod in YddSplitter.LodTags)
            {
                if (!piece.Geometries.TryGetValue(lod, out var geoms) || geoms.Count == 0) continue;
                var srcModel = (XmlElement?)template.SelectSingleNode($"{lod}/Item") ?? (XmlElement)template.SelectSingleNode("DrawableModelsHigh/Item")!;
                var lodEl = doc.CreateElement(lod);
                var model = doc.CreateElement("Item");
                foreach (var tag in new[] { "RenderMask", "Flags", "HasSkin", "BoneIndex", "Unknown1" })
                    if (srcModel.SelectSingleNode(tag) is XmlNode n) model.AppendChild(n.CloneNode(true));
                var ge = doc.CreateElement("Geometries");
                foreach (var g in geoms)
                {
                    var gi = (XmlElement)g.SelectSingleNode("ShaderIndex")!;
                    gi.SetAttribute("value", remap[int.Parse(gi.GetAttribute("value"), CultureInfo.InvariantCulture)].ToString(CultureInfo.InvariantCulture));
                    ge.AppendChild(g);
                }
                model.AppendChild(ge);
                lodEl.AppendChild(model);
                x.AppendChild(lodEl);
            }
            x.AppendChild(doc.CreateElement("Lights"));

            int mainShader = remap[piece.ShaderUse.MaxBy(kv => kv.Value).Key];
            var (dif, nrm, spec) = RenameMainTextures(x, mainShader, comp, 0);
            list.Add(new OutDrawable(dname, comp, 0, x, dif, nrm, spec));
        }
        if (!pieces.ContainsKey("head")) warnings.Add("No triangles were skinned to head or neck bones, so the ped has no head component.");
        return list;
    }

    static int MainShader(XmlElement drawable)
    {
        var counts = new Dictionary<int, int>();
        foreach (XmlElement g in drawable.SelectNodes("DrawableModelsHigh/Item/Geometries/Item")!)
        {
            int s = int.Parse(((XmlElement)g.SelectSingleNode("ShaderIndex")!).GetAttribute("value"), CultureInfo.InvariantCulture);
            int tris = (g.SelectSingleNode("IndexBuffer/Data")?.InnerText.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length ?? 0) / 3;
            counts[s] = counts.GetValueOrDefault(s) + tris;
        }
        return counts.Count == 0 ? 0 : counts.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>Points the main shader's samplers at the convention names; returns the original texture names.</summary>
    static (string? diffuse, string? normal, string? spec) RenameMainTextures(XmlElement drawable, int shaderIndex, PedComponents.Comp comp, int index)
    {
        var shader = drawable.SelectNodes("ShaderGroup/Shaders/Item")!.OfType<XmlElement>().ElementAtOrDefault(shaderIndex);
        if (shader == null) return (null, null, null);
        string? Swap(string sampler, string newName)
        {
            var n = shader.SelectSingleNode($"Parameters/Item[@name='{sampler}']/Name");
            if (n == null) return null;
            var old = n.InnerText;
            n.InnerText = newName;
            return string.IsNullOrEmpty(old) ? null : old;
        }
        return (Swap("DiffuseSampler", PedComponents.DiffuseName(comp, index)),
                Swap("BumpSampler", PedComponents.NormalName(comp, index)),
                Swap("SpecSampler", PedComponents.SpecName(comp, index)));
    }

    static void ReplaceSkeleton(XmlElement drawable, XmlElement skeleton)
    {
        var old = drawable.SelectSingleNode("Skeleton");
        var copy = drawable.OwnerDocument.ImportNode(skeleton, true);
        if (old != null) drawable.ReplaceChild(copy, old);
        else drawable.InsertAfter(copy, drawable.SelectSingleNode("ShaderGroup"));
    }

    // ---------- yft / ytd ----------

    static byte[] BuildYft(string name, YftFile? src, XmlElement skeleton, (uint, uint, uint) sig, string work, List<string> checks, List<string> warnings)
    {
        if (src?.Fragment != null)
        {
            var sk = src.Fragment.Drawable?.Skeleton;
            if (sk != null)
            {
                sk.Unknown_50h = sig.Item1;
                sk.Unknown_54h = sig.Item2;
                sk.Unknown_58h = sig.Item3;
            }
            src.Fragment.Name = "pack:/" + name;
            var bytes = src.Save();
            GtaIO.LoadYft(bytes, name + ".yft");
            checks.Add("YFT: your .yft, renamed and given unique skeleton signatures");
            return bytes;
        }

        // No .yft uploaded: build one that carries the skeleton only (no ragdoll physics).
        warnings.Add("No .yft was uploaded, so a skeleton-only .yft was generated (no ragdoll physics). " +
                     "For full ragdoll behaviour, also upload the .yft of a ped with the same skeleton.");
        var doc = new XmlDocument();
        var frag = (XmlElement)doc.AppendChild(doc.CreateElement("Fragment"))!;
        AddText(doc, frag, "Name", "pack:/" + name);
        AddVec(doc, frag, "BoundingSphereCenter", new Vector3(0, 0, 0));
        AddValue(doc, frag, "BoundingSphereRadius", "1.5");
        foreach (var (k, v) in new[] { ("UnknownB0", "0"), ("UnknownB8", "0"), ("UnknownBC", "0"), ("UnknownC0", "65280"), ("UnknownC4", "1"), ("UnknownCC", "0"), ("GravityFactor", "1"), ("BuoyancyFactor", "1") })
            AddValue(doc, frag, k, v);
        var d = doc.CreateElement("Drawable");
        AddText(doc, d, "Name", "skel");
        var m = doc.CreateElement("Matrix");
        m.InnerText = "\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        d.AppendChild(m);
        AddVec(doc, d, "BoundingSphereCenter", Vector3.Zero);
        AddValue(doc, d, "BoundingSphereRadius", "1.5");
        AddVec(doc, d, "BoundingBoxMin", new Vector3(-1, -1, -1));
        AddVec(doc, d, "BoundingBoxMax", new Vector3(1, 1, 1));
        foreach (var t in new[] { "LodDistHigh", "LodDistMed", "LodDistLow", "LodDistVlow" }) AddValue(doc, d, t, "9998");
        d.AppendChild(doc.ImportNode(skeleton, true));
        frag.AppendChild(d);
        var bytes2 = GtaIO.YftFromXml(doc.OuterXml, work);
        var back = GtaIO.LoadYft(bytes2, name + ".yft");
        int bones = back.Fragment?.Drawable?.Skeleton?.Bones?.Items?.Length ?? 0;
        if (bones == 0) throw new GtaFormatException("Generated .yft has no skeleton after read-back.");
        checks.Add($"YFT: generated, skeleton {bones} bones");
        return bytes2;
    }

    static byte[] BuildYtdFromDds(string dir, List<string> names)
    {
        var x = new Xw();
        x.Open("TextureDictionary");
        foreach (var n in names)
        {
            x.Open("Item");
            x.Text("Name", n);
            x.Value("Unk32", 0);
            x.Text("Usage", "UNKNOWN");
            x.Text("UsageFlags", "0");
            x.Value("ExtraFlags", 0);
            x.Value("Width", 4);
            x.Value("Height", 4);
            x.Value("MipLevels", 1);
            x.Text("Format", "D3DFMT_DXT5");
            x.Text("FileName", n + ".dds");
            x.Close("Item");
        }
        x.Close("TextureDictionary");
        return GtaIO.YtdFromXml(x.ToString(), dir);
    }

    // ---------- XML helpers ----------

    static void AddText(XmlDocument d, XmlElement p, string tag, string text)
    {
        var e = d.CreateElement(tag); e.InnerText = text; p.AppendChild(e);
    }

    static void AddValue(XmlDocument d, XmlElement p, string tag, string value)
    {
        var e = d.CreateElement(tag); e.SetAttribute("value", value); p.AppendChild(e);
    }

    static void AddVec(XmlDocument d, XmlElement p, string tag, Vector3 v)
    {
        var e = d.CreateElement(tag);
        e.SetAttribute("x", v.X.ToString(CultureInfo.InvariantCulture));
        e.SetAttribute("y", v.Y.ToString(CultureInfo.InvariantCulture));
        e.SetAttribute("z", v.Z.ToString(CultureInfo.InvariantCulture));
        p.AppendChild(e);
    }

    static void SetValue(XmlElement p, string tag, uint value)
    {
        if (p.SelectSingleNode(tag) is XmlElement e) e.SetAttribute("value", value.ToString(CultureInfo.InvariantCulture));
        else AddValue(p.OwnerDocument, p, tag, value.ToString(CultureInfo.InvariantCulture));
    }

    // ---------- resource files ----------

    static string Manifest(string name, bool test)
    {
        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine("lua54 'yes'");
        sb.AppendLine();
        sb.AppendLine($"description '{name} addon ped - generated by GTA Forge'");
        sb.AppendLine();
        sb.AppendLine("files {");
        sb.AppendLine("    'peds.meta',");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("data_file 'PED_METADATA_FILE' 'peds.meta'");
        if (test)
        {
            sb.AppendLine();
            sb.AppendLine("client_script 'client/test_ped.lua'");
        }
        return sb.ToString();
    }

    static string TestScript(string name) => $$"""
        -- Test command: /{{name}} turns you into the ped.
        RegisterCommand('{{name}}', function()
            local model = `{{name}}`
            if not IsModelInCdimage(model) then
                print('^1{{name}}: model not found. Is the resource started (ensure {{name}})?^0')
                return
            end
            RequestModel(model)
            local t = GetGameTimer()
            while not HasModelLoaded(model) and GetGameTimer() - t < 10000 do Wait(0) end
            SetPlayerModel(PlayerId(), model)
            SetPedDefaultComponentVariation(PlayerPedId())
            SetModelAsNoLongerNeeded(model)
        end, false)
        """;

    static string Readme(string name, List<OutDrawable> drawables, PedRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {name} (addon ped)").AppendLine().AppendLine("Generated by GTA Forge.").AppendLine();
        sb.AppendLine("## Install (FiveM)");
        sb.AppendLine($"1. Copy the `{name}` folder into your server's `resources` folder.");
        sb.AppendLine($"2. Add `ensure {name}` to `server.cfg` and restart.");
        if (req.TestCommand) sb.AppendLine($"3. In game, type `/{name}` to become the ped.");
        sb.AppendLine().AppendLine("## Files").AppendLine("| file | what it is |").AppendLine("|---|---|");
        sb.AppendLine($"| `stream/{name}.ydd` | body parts: {string.Join(", ", drawables.Select(d => $"`{d.Name}`"))} |");
        sb.AppendLine($"| `stream/{name}.ytd` | textures, named `<part>_diff_000_a_uni` so the game's variation system finds them |");
        sb.AppendLine($"| `stream/{name}.yft` | skeleton |");
        sb.AppendLine($"| `stream/{name}.ymt` | variation info (which parts and textures exist) |");
        sb.AppendLine($"| `peds.meta` | ped definition (type `{req.PedType}`, movement, voice, behaviour) |");
        sb.AppendLine().AppendLine("## Spawn from a script").AppendLine("```lua");
        sb.AppendLine($"RequestModel(`{name}`)");
        sb.AppendLine($"while not HasModelLoaded(`{name}`) do Wait(0) end");
        sb.AppendLine($"local ped = CreatePed(4, `{name}`, x, y, z, heading, true, true)");
        sb.AppendLine("```");
        return sb.ToString();
    }
}
