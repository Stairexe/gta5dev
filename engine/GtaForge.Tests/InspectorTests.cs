using GtaForge.Engine.Gta;
using GtaForge.Engine.Inspect;

namespace GtaForge.Tests;

public class InspectorTests
{
    [Fact]
    public void Ped_ydd_preview_has_meshes_bones_and_texture()
    {
        var png = TextureWriter.SolidRgbaPng(200, 150, 120);
        var ytd = TextureWriter.BuildYtd([new("body_diff", png, TextureKind.Diffuse)], Directory.CreateTempSubdirectory("gf_i_").FullName);
        var p = Inspector.Inspect("body.ydd", PedBuilderTests.SyntheticPedYdd(), ytd);
        Assert.Equal("ydd", p.Kind);
        Assert.Single(p.Meshes);
        Assert.Equal(7 * 12, p.Triangles);
        Assert.Equal(9, p.Bones.Count);
        Assert.StartsWith("data:image/png;base64,", p.Textures["body_diff"]);
    }

    [Fact]
    public void Sollumz_ydr_preview_reads_geometry()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "model_with_embedded_textures.ydr"));
        var p = Inspector.Inspect("model_with_embedded_textures.ydr", bytes);
        Assert.NotEmpty(p.Meshes);
        Assert.NotEmpty(p.Textures);
    }
}
