using Source2.Compiler.Compiling;
using Source2.Compiler.RcTexture;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>A caller-made mip chain (<c>MipChainBgra</c>) is checked and then used exactly as given.</summary>
public class MipChainTests
{
    // A chain whose level i is filled with one BGRA colour, distinct per level.
    private static byte[][] SolidChain(int width, int height, int levels)
    {
        var chain = new byte[levels][];
        for (var i = 0; i < levels; i++)
        {
            var w = Math.Max(1, width >> i);
            var h = Math.Max(1, height >> i);
            chain[i] = new byte[w * h * 4];
            for (var p = 0; p < w * h; p++)
            {
                chain[i][p * 4] = (byte)(10 * i + 1);
                chain[i][p * 4 + 1] = (byte)(10 * i + 2);
                chain[i][p * 4 + 2] = (byte)(10 * i + 3);
                chain[i][p * 4 + 3] = 255;
            }
        }
        return chain;
    }

    [Fact]
    public void BuildTexture_StoresTheGivenLevelsUnchanged()
    {
        var chain = SolidChain(16, 8, 5);
        var def = new ResourceBuilder.TextureDef
        {
            RawRgba = new byte[16 * 8 * 4],
            RawWidth = 16,
            RawHeight = 8,
            Compression = ResourceBuilder.TextureCompression.None,
            MipChainBgra = chain,
        };
        var output = ResourceBuilder.BuildTexture(def);

        // Uncompressed mips follow the container, smallest first.
        var tail = chain.Reverse().SelectMany(level => level).ToArray();
        Assert.True(output.AsSpan(output.Length - tail.Length).SequenceEqual(tail));
    }

    [Fact]
    public void BuildTexture_RefusesALevelOfTheWrongSize()
    {
        var chain = SolidChain(16, 8, 3);
        chain[1] = new byte[7];
        var def = new ResourceBuilder.TextureDef
        {
            RawRgba = new byte[16 * 8 * 4],
            RawWidth = 16,
            RawHeight = 8,
            MipChainBgra = chain,
        };
        Assert.Throws<ArgumentException>(() => ResourceBuilder.BuildTexture(def));
    }

    [Fact]
    public void VtexCompile_EncodesTheGivenLevels()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        var textures = Path.Combine(dir!, "RcReference", "textures");
        var image = File.ReadAllBytes(Path.Combine(textures, "probe.png"));
        var (_, width, height) = VtexCompiler.DecodeRgba(image, "probe.png");
        var levels = 0;
        for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            levels++;
            if (w == 1 && h == 1)
                break;
        }
        // Noise, so no level compresses and each is stored as written.
        var random = new Random(7);
        var chain = Enumerable.Range(0, levels).Select(i =>
        {
            var level = new byte[Math.Max(1, width >> i) * Math.Max(1, height >> i) * 4];
            random.NextBytes(level);
            return level;
        }).ToArray();

        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("materials/s2ctex/rgba.vtex", File.ReadAllBytes(Path.Combine(textures, "rgba.vtex")))
        {
            Content = new MemoryContentSource().Add("materials/s2ctex/probe.png", image),
            Settings = new VtexCompileSettings { MipChainBgra = chain },
        });
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));

        // RGBA8888: each level is the given one, red and blue swapped, stored smallest first.
        var tail = chain.Reverse().SelectMany(VtexCompiler.SwapRedBlue).ToArray();
        Assert.True(result.Output!.AsSpan(result.Output!.Length - tail.Length).SequenceEqual(tail));
    }
}
