using System.IO.Compression;
using System.Text;

namespace GtaForge.Engine.Packaging;

/// <summary>Collects the files of a FiveM resource and zips them under a root folder.</summary>
public sealed class ResourceZip(string resourceName)
{
    readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public string ResourceName { get; } = resourceName;
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public void Add(string path, byte[] data) => _files[path.Replace('\\', '/')] = data;
    public void Add(string path, string text) => Add(path, Encoding.UTF8.GetBytes(text));

    public byte[] ToZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (path, data) in _files.OrderBy(f => f.Key))
            {
                var e = zip.CreateEntry($"{ResourceName}/{path}", CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }
}
