using Source2.Compiler.Materials;
using Source2.Compiler.Shaders;
using ValvePak;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Which child textures a <c>.vmat</c> compiles to, from the shader's programs, against
/// resourcecompiler (RcReference/materials): the logical-CRC text RC logged for each child, the
/// child names in RED2 and the texture params in DATA. Needs CS2's shaders and content.
/// </summary>
public class MaterialTextureTests
{
    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "RcReference", "materials", name);
    }

    private static readonly Lazy<Package?> Shaders = new(() =>
    {
        if (CS2Fixtures.StockPak() is not { } pak || Path.Combine(Path.GetDirectoryName(pak)!, "shaders_pc_dir.vpk") is var path && !File.Exists(path))
            return null;
        var package = new Package();
        package.Read(path);
        return package;
    });

    private static ShaderProgram Program(string shader, string kind)
    {
        var entry = Shaders.Value!.FindEntry($"shaders/vfx/{Path.GetFileNameWithoutExtension(shader)}_pc_50_{kind}.vcs")!;
        Shaders.Value.ReadEntry(entry, out var bytes);
        return ShaderProgram.Read(bytes);
    }

    // content/csgo and content/core, beside the game folder.
    private static bool ContentExists(string path)
    {
        var game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!)!)!;
        return new[] { "csgo", "core" }.Any(mod => File.Exists(Path.Combine(game, "content", mod, path)));
    }

    [Theory]
    [InlineData("cx_color")]
    [InlineData("cx_full")]
    [InlineData("cx_translucent")]
    [InlineData("cx_params")]
    [InlineData("cx_vtex")]
    [InlineData("char")]
    [InlineData("unlit")]
    [InlineData("cx_ao256")]
    [InlineData("cx_tiny")]
    [InlineData("cx_odd")]
    [InlineData("cx_metal_noise")]
    [InlineData("cx_order")]
    public void ChildTextures_MatchResourceCompiler(string name)
    {
        if (Shaders.Value is null) { CS2Fixtures.Skip("shaders_pc_dir.vpk"); return; }
        var vmat = VmatSource.Parse(File.ReadAllBytes(Fixture(name + ".vmat")), name + ".vmat");
        var textures = MaterialTextures.Resolve(Program(vmat.Shader, "features"), Program(vmat.Shader, "ps"), vmat.ByName, ContentExists);

        var rc = CompiledResource.Read(File.ReadAllBytes(Fixture($"compiled/{name}.vmat_c")));
        var rcParams = rc.Kv3("DATA")!["m_textureParams"]!.Values
            .Select(p => ((string)p["m_name"]!, (string)p["m_pValue"]!)).ToList();
        Assert.Equal(rcParams, textures.Select(t => (t.Name, t.ResourcePath)).ToList());

        var rcChildren = rc.Kv3("RED2")!["m_ChildResourceList"]!.Values.Select(v => (string)v).ToList();
        Assert.Equal(rcChildren, textures.Where(t => t.Generated is not null).Select(t => t.ResourcePath).Order(StringComparer.Ordinal).ToList());

        foreach (var t in textures.Where(t => t.Generated is not null))
            Assert.Equal(File.ReadAllText(Fixture($"spew/{Path.GetFileName(t.ResourcePath)}.txt")).ReplaceLineEndings("\n"), t.Generated!.LogicalText());
    }

    [Fact]
    public void LogicalCrc_IsMurmurHash2OfTheText()
    {
        foreach (var file in Directory.GetFiles(Fixture("spew"), "*.txt"))
        {
            var text = File.ReadAllText(file).ReplaceLineEndings("\n");
            var crc = GeneratedTexture.MurmurHash2(System.Text.Encoding.UTF8.GetBytes(text), GeneratedTexture.LogicalCrcSeed);
            Assert.EndsWith($"_{crc:x}.vtex.txt", Path.GetFileName(file));
        }
    }
}
