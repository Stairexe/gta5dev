using System.Text;
using System.Text.Json;
using GtaForge.Core.Gta;
using GtaForge.Core.Inspect;
using GtaForge.Core.Peds;
using GtaForge.Core.Props;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

const long MaxUpload = 200L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUpload;
    o.ValueLengthLimit = int.MaxValue;
});
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, engine = "CodeWalker.Core (MIT)" }));

// ---- Props: GLB (converted in the browser) -> FiveM prop resource ----
app.MapPost("/api/props/build", async (HttpRequest http) =>
{
    var form = await http.ReadFormAsync();
    var spec = form["spec"].ToString();
    if (string.IsNullOrWhiteSpace(spec)) return Problem("Missing 'spec' field.");
    var req = JsonSerializer.Deserialize<PropRequest>(spec, json) ?? new PropRequest();
    var files = await ReadFiles(form);
    return Run(() =>
    {
        var result = PropBuilder.Build(req, files);
        return ZipResult(result.Resource.ToZip(), result.Resource.ResourceName, result.Report);
    });
});

// ---- Inspect: any YDR/YDD/YFT -> preview JSON for the 3D viewer ----
app.MapPost("/api/inspect", async (HttpRequest http) =>
{
    var form = await http.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file == null) return Problem("Upload a .ydr, .ydd or .yft file.");
    var bytes = await ReadAll(file);
    var ytdFile = form.Files.GetFile("ytd");
    var ytd = ytdFile != null ? await ReadAll(ytdFile) : null;
    return Run(() => Results.Json(Inspector.Inspect(file.FileName, bytes, ytd), json));
});

// ---- Peds: rigged .ydd (+ .ytd, .yft) -> addon ped resource ----
app.MapPost("/api/peds/from-ydd", async (HttpRequest http) =>
{
    var form = await http.ReadFormAsync();
    var spec = form["spec"].ToString();
    var req = string.IsNullOrWhiteSpace(spec) ? new PedRequest() : JsonSerializer.Deserialize<PedRequest>(spec, json) ?? new PedRequest();
    var ydd = form.Files.GetFile("ydd");
    if (ydd == null) return Problem("Upload the ped's .ydd file.");
    var ytd = form.Files.GetFile("ytd");
    var yft = form.Files.GetFile("yft");
    var input = new PedInput(
        await ReadAll(ydd),
        ytd != null ? await ReadAll(ytd) : null,
        yft != null ? await ReadAll(yft) : null);
    return Run(() =>
    {
        var result = PedBuilder.Build(req, input);
        return ZipResult(result.Resource.ToZip(), result.Resource.ResourceName, result.Report);
    });
});

app.MapFallbackToFile("index.html");
app.Run();

static IResult Problem(string message, int status = 400) =>
    Results.Json(new { error = message }, statusCode: status);

static IResult Run(Func<IResult> work)
{
    try { return work(); }
    catch (ArgumentException e) { return Problem(e.Message); }
    catch (GtaFormatException e) { return Problem(e.Message, 422); }
    catch (Exception e) { return Problem("Build failed: " + e.Message, 500); }
}

static IResult ZipResult(byte[] zip, string name, BuildReport report)
{
    var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    return new ZipWithReport(zip, name + ".zip", Convert.ToBase64String(Encoding.UTF8.GetBytes(reportJson)));
}

static async Task<byte[]> ReadAll(IFormFile f)
{
    using var ms = new MemoryStream();
    await f.CopyToAsync(ms);
    return ms.ToArray();
}

static async Task<Dictionary<string, byte[]>> ReadFiles(IFormCollection form)
{
    var d = new Dictionary<string, byte[]>();
    foreach (var f in form.Files) d[f.Name] = await ReadAll(f);
    return d;
}

sealed class ZipWithReport(byte[] zip, string fileName, string reportB64) : IResult
{
    public async Task ExecuteAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        ctx.Response.Headers["X-Build-Report"] = reportB64;
        ctx.Response.Headers.AccessControlExposeHeaders = "X-Build-Report, Content-Disposition";
        await ctx.Response.Body.WriteAsync(zip);
    }
}

public partial class Program;
