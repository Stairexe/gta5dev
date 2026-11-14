using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;

namespace GtaForge.Engine.Gta;

/// <summary>Tiny indented XML writer producing CodeWalker-style XML.</summary>
public sealed class Xw
{
    readonly StringBuilder _sb = new();
    int _depth;

    public Xw() => _sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");

    public static string F(float v) => v.ToString("0.0#######", CultureInfo.InvariantCulture);
    public static string F(double v) => ((float)v).ToString("0.0#######", CultureInfo.InvariantCulture);
    public static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    void Indent() => _sb.Append(' ', _depth);

    public Xw Open(string tag, string attrs = "")
    {
        Indent();
        _sb.Append('<').Append(tag);
        if (attrs.Length > 0) _sb.Append(' ').Append(attrs);
        _sb.Append(">\n");
        _depth++;
        return this;
    }

    public Xw Close(string tag)
    {
        _depth--;
        Indent();
        _sb.Append("</").Append(tag).Append(">\n");
        return this;
    }

    public Xw Empty(string tag, string attrs = "")
    {
        Indent();
        _sb.Append('<').Append(tag);
        if (attrs.Length > 0) _sb.Append(' ').Append(attrs);
        _sb.Append(" />\n");
        return this;
    }

    public Xw Text(string tag, string text)
    {
        Indent();
        if (string.IsNullOrEmpty(text)) _sb.Append('<').Append(tag).Append(" />\n");
        else _sb.Append('<').Append(tag).Append('>').Append(Esc(text)).Append("</").Append(tag).Append(">\n");
        return this;
    }

    public Xw Value(string tag, string value) => Empty(tag, $"value=\"{value}\"");
    public Xw Value(string tag, int value) => Value(tag, value.ToString(CultureInfo.InvariantCulture));
    public Xw Value(string tag, uint value) => Value(tag, value.ToString(CultureInfo.InvariantCulture));
    public Xw Value(string tag, float value) => Value(tag, F(value));
    public Xw Value(string tag, bool value) => Value(tag, value ? "true" : "false");
    public Xw Vec3(string tag, Vector3 v) => Empty(tag, $"x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\"");
    public Xw Vec4(string tag, Vector4 v) => Empty(tag, $"x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\" w=\"{F(v.W)}\"");
    public Xw Quat(string tag, Quaternion q) => Vec4(tag, new Vector4(q.X, q.Y, q.Z, q.W));

    /// <summary>Raw multi-line content (vertex/index data) at the current depth.</summary>
    public Xw Raw(string tag, IEnumerable<string> lines)
    {
        Indent();
        _sb.Append('<').Append(tag).Append(">\n");
        foreach (var l in lines)
        {
            _sb.Append(' ', _depth + 1).Append(l).Append('\n');
        }
        Indent();
        _sb.Append("</").Append(tag).Append(">\n");
        return this;
    }

    /// <summary>Appends a fragment produced by another <see cref="Xw"/> (without its XML declaration), re-indented to the current depth.</summary>
    public Xw Fragment(string xml)
    {
        foreach (var line in xml.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("<?xml")) continue;
            Indent();
            _sb.Append(line).Append('\n');
        }
        return this;
    }

    public override string ToString() => _sb.ToString();
}
