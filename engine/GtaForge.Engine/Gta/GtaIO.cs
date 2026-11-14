using System.Xml;
using CodeWalker.GameFiles;

namespace GtaForge.Engine.Gta;

/// <summary>
/// Thin wrapper over CodeWalker.Core for loading compiled RAGE resources from raw bytes
/// and for turning CodeWalker XML back into compiled resources.
/// </summary>
public static class GtaIO
{
    /// <summary>RSC7 magic ("RSC7" little-endian).</summary>
    public const uint Rsc7Magic = 0x37435352;

    public static bool IsRsc7(byte[] data) =>
        data.Length >= 16 && BitConverter.ToUInt32(data, 0) == Rsc7Magic;

    static RpfFileEntry Entry(ref byte[] data, string name)
    {
        if (!IsRsc7(data))
            throw new GtaFormatException($"{name} is not a compiled RAGE resource (missing RSC7 header). " +
                                         "Export it from CodeWalker/OpenIV as the raw file, not XML.");
        var entry = RpfFile.CreateResourceFileEntry(ref data, 0);
        entry.Name = name;
        entry.NameLower = name.ToLowerInvariant();
        return entry;
    }

    public static YdrFile LoadYdr(byte[] data, string name = "model.ydr")
    {
        var e = Entry(ref data, name);
        var f = new YdrFile();
        f.Load(ResourceBuilder.Decompress(data), e);
        return f;
    }

    public static YddFile LoadYdd(byte[] data, string name = "model.ydd")
    {
        var e = Entry(ref data, name);
        var f = new YddFile();
        f.Load(ResourceBuilder.Decompress(data), e);
        return f;
    }

    public static YtdFile LoadYtd(byte[] data, string name = "model.ytd")
    {
        var e = Entry(ref data, name);
        var f = new YtdFile();
        f.Load(ResourceBuilder.Decompress(data), e);
        return f;
    }

    public static YftFile LoadYft(byte[] data, string name = "model.yft")
    {
        var e = Entry(ref data, name);
        var f = new YftFile();
        f.Load(ResourceBuilder.Decompress(data), e);
        return f;
    }

    public static YcdFile LoadYcd(byte[] data, string name = "anim.ycd")
    {
        var e = Entry(ref data, name);
        var f = new YcdFile();
        f.Load(ResourceBuilder.Decompress(data), e);
        return f;
    }

    public static YtypFile LoadYtyp(byte[] data, string name = "types.ytyp")
    {
        var e = Entry(ref data, name);
        var f = new YtypFile();
        f.Load(ResourceBuilder.Decompress(data), e);
        return f;
    }

    /// <summary>Loads a .ymt. Ped variation ymts are PSO (not RSC7); both are handled.</summary>
    public static YmtFile LoadYmt(byte[] data, string name = "ped.ymt")
    {
        var f = new YmtFile();
        if (IsRsc7(data))
        {
            var e = Entry(ref data, name);
            f.Load(ResourceBuilder.Decompress(data), e);
        }
        else
        {
            var e = new RpfBinaryFileEntry { Name = name, NameLower = name.ToLowerInvariant(), FileSize = (uint)data.Length };
            f.Load(data, e);
        }
        return f;
    }

    static XmlDocument Doc(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    /// <summary>Compiles CodeWalker drawable XML. <paramref name="ddsFolder"/> holds any embedded .dds files.</summary>
    public static byte[] YdrFromXml(string xml, string ddsFolder = "") =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.Ydr, ddsFolder), "YDR");

    public static byte[] YddFromXml(string xml, string ddsFolder = "") =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.Ydd, ddsFolder), "YDD");

    public static byte[] YftFromXml(string xml, string ddsFolder = "") =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.Yft, ddsFolder), "YFT");

    public static byte[] YtdFromXml(string xml, string ddsFolder) =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.Ytd, ddsFolder), "YTD");

    public static byte[] YcdFromXml(string xml) =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.Ycd, ""), "YCD");

    public static byte[] YbnFromXml(string xml) =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.Ybn, ""), "YBN");

    /// <summary>RSC meta (.ytyp, .ymap).</summary>
    public static byte[] RscMetaFromXml(string xml) =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.RSC, ""), "RSC meta");

    /// <summary>PSO meta (ped variation .ymt).</summary>
    public static byte[] PsoFromXml(string xml) =>
        Require(XmlMeta.GetData(Doc(xml), MetaFormat.PSO, ""), "PSO meta");

    static byte[] Require(byte[]? data, string what) =>
        data ?? throw new GtaFormatException($"CodeWalker could not build the {what} from the generated XML.");
}

public class GtaFormatException(string message) : Exception(message);
