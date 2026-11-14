using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using GtaForge.Core.Gta;
using GtaForge.Core.Inspect;
using GtaForge.Core.Peds;
using GtaForge.Core.Props;

// Entry point is required by the WebAssembly host; all work happens in the exported methods.
Console.WriteLine("GTA Forge engine ready");

/// <summary>
/// JavaScript bridge. Files are staged one at a time with <see cref="AddFile"/> (JS cannot pass
/// arrays of byte arrays), then a build call returns the zip and leaves its report in <see cref="LastReport"/>.
/// Errors never throw across the boundary: the call returns null and <see cref="LastError"/> says why.
/// </summary>
public static partial class Engine
{
    static readonly Dictionary<string, byte[]> Staged = new();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static string _report = "{}";
    static string _error = "";

    [JSExport] public static void ClearFiles() => Staged.Clear();
    [JSExport] public static void AddFile(string field, byte[] data) => Staged[field] = data;
    [JSExport] public static string LastReport() => _report;
    [JSExport] public static string LastError() => _error;

    [JSExport]
    public static byte[]? BuildProp(string specJson)
    {
        return Run(() =>
        {
            var req = JsonSerializer.Deserialize<PropRequest>(specJson, Json) ?? new PropRequest();
            var r = PropBuilder.Build(req, new Dictionary<string, byte[]>(Staged));
            _report = JsonSerializer.Serialize(r.Report, Json);
            return r.Resource.ToZip();
        });
    }

    [JSExport]
    public static byte[]? BuildPed(string specJson)
    {
        return Run(() =>
        {
            var req = JsonSerializer.Deserialize<PedRequest>(specJson, Json) ?? new PedRequest();
            if (!Staged.TryGetValue("ydd", out var ydd)) throw new ArgumentException("Upload the ped's .ydd file.");
            var r = PedBuilder.Build(req, new PedInput(ydd, Staged.GetValueOrDefault("ytd"), Staged.GetValueOrDefault("yft")));
            _report = JsonSerializer.Serialize(r.Report, Json);
            return r.Resource.ToZip();
        });
    }

    /// <summary>Preview JSON for a staged "file" (+ optional staged "ytd").</summary>
    [JSExport]
    public static string? Inspect(string fileName)
    {
        var bytes = Run(() =>
        {
            if (!Staged.TryGetValue("file", out var data)) throw new ArgumentException("No file staged.");
            var p = Inspector.Inspect(fileName, data, Staged.GetValueOrDefault("ytd"));
            return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p, Json));
        });
        return bytes == null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    static byte[]? Run(Func<byte[]> work)
    {
        _error = "";
        try { return work(); }
        catch (ArgumentException e) { _error = e.Message; }
        catch (GtaFormatException e) { _error = e.Message; }
        catch (Exception e) { _error = "Build failed: " + e.Message; }
        return null;
    }
}
