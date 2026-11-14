using CodeWalker.GameFiles;
using GtaForge.Engine.Gta;

namespace GtaForge.Tests;

public class RoundTripTests
{
    static byte[] Asset(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", name));

    [Theory]
    [InlineData("model_with_simple_bounds.ydr")]
    [InlineData("model_with_embedded_textures.ydr")]
    public void Ydr_binary_to_xml_to_binary_preserves_geometry(string file)
    {
        var original = GtaIO.LoadYdr(Asset(file), file);
        Assert.NotNull(original.Drawable);
        var tmp = Directory.CreateTempSubdirectory("gf_rt_").FullName;

        var xml = YdrXml.GetXml(original, tmp);
        var rebuilt = GtaIO.LoadYdr(GtaIO.YdrFromXml(xml, tmp), file);

        var a = original.Drawable.DrawableModels.High;
        var b = rebuilt.Drawable.DrawableModels.High;
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Geometries.Length, b[i].Geometries.Length);
            for (int g = 0; g < a[i].Geometries.Length; g++)
            {
                Assert.Equal(a[i].Geometries[g].VerticesCount, b[i].Geometries[g].VerticesCount);
                Assert.Equal(a[i].Geometries[g].IndicesCount, b[i].Geometries[g].IndicesCount);
            }
        }
        Assert.Equal(original.Drawable.Bound?.Type, rebuilt.Drawable.Bound?.Type);
    }
}
