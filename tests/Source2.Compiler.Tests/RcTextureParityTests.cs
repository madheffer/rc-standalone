using SkiaSharp;
using Source2.Compiler.RcTexture;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The texture pipeline against resourcecompiler's own output: probe PNGs compiled by RC on
/// 2026-10-13 (RcReference/textures, with their .vtex sources). Every mip of every RGBA8888,
/// DXT1 and DXT5 file must come out byte for byte; ATI1N/ATI2N mip 0 likewise (their lower
/// mips need the linear chain, still open).
/// </summary>
public class RcTextureParityTests
{
    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "RcReference", "textures", name);
    }

    private static (byte[] Rgba, int Width, int Height) LoadPng(string name)
    {
        using var codec = SKCodec.Create(Fixture(name))!;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, out var pixels));
        return (pixels, info.Width, info.Height);
    }

    private static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        return bgra;
    }

    // RC stores mips smallest first at the end of the file.
    private static void AssertMips(string png, string vtexC, string format, int mips)
    {
        var (rgba, w, h) = LoadPng(png);
        var levels = RcMipChain.Build(rgba, w, h, srgb: true);
        var file = File.ReadAllBytes(Fixture(vtexC));
        int Size(RcMipChain.Level l) => format == "rgba" ? l.Width * l.Height * 4
            : ((l.Width + 3) / 4) * ((l.Height + 3) / 4) * (format == "dxt1" ? 8 : 16);
        var offsets = new int[mips];
        int at = file.Length - Enumerable.Range(0, mips).Sum(i => Size(levels[i]));
        for (int i = mips - 1; i >= 0; i--) { offsets[i] = at; at += Size(levels[i]); }

        for (int i = 0; i < mips; i++)
        {
            var bytes = RcMipChain.WriteRgba8(levels[i], srgb: true);
            var mine = format == "rgba" ? bytes : AtiCompress.Compress(ToBgra(bytes), levels[i].Width, levels[i].Height,
                format == "dxt1" ? AtiCompress.Format.Dxt1 : AtiCompress.Format.Dxt5, perceptual: true);
            Assert.True(file.AsSpan(offsets[i], Size(levels[i])).SequenceEqual(mine),
                $"{vtexC}: mip {i} ({levels[i].Width}x{levels[i].Height}) differs from resourcecompiler's");
        }
    }

    [Theory]
    [InlineData("probe.png", "rgba.vtex_c", "rgba", 9)]
    [InlineData("probe_npot.png", "rgba_npot.vtex_c", "rgba", 9)]
    [InlineData("probe_7x5.png", "rgba_7x5.vtex_c", "rgba", 3)]
    [InlineData("probe.png", "dxt1.vtex_c", "dxt1", 7)]
    [InlineData("probe.png", "dxt5.vtex_c", "dxt5", 7)]
    [InlineData("probe_npot.png", "dxt1_npot.vtex_c", "dxt1", 6)]
    [InlineData("probe_opaque.png", "dxt1_opaque.vtex_c", "dxt1", 7)]
    // An all-opaque DXT5 request is written as DXT1.
    [InlineData("probe_7x5.png", "dxt5_7x5.vtex_c", "dxt1", 1)]
    public void EveryMip_MatchesResourceCompiler(string png, string vtexC, string format, int mips)
        => AssertMips(png, vtexC, format, mips);

    // ATI1N/ATI2N are linear: weights 1, and the linear loader (v * (1/255f) on a width that is
    // a multiple of 4).
    [Theory]
    [InlineData("ati1n.vtex_c", AtiCompress.Format.Ati1n)]
    [InlineData("ati2n.vtex_c", AtiCompress.Format.Ati2n)]
    public void AtiEveryMip_MatchesResourceCompiler(string vtexC, AtiCompress.Format format)
    {
        var (rgba, w, h) = LoadPng("probe.png");
        var levels = RcMipChain.Build(rgba, w, h, srgb: false);
        var file = File.ReadAllBytes(Fixture(vtexC));
        int bs = AtiCompress.BlockSize(format), mips = 7;
        int Size(RcMipChain.Level l) => ((l.Width + 3) / 4) * ((l.Height + 3) / 4) * bs;
        int at = file.Length;
        for (int i = 0; i < mips; i++)
        {
            at -= Size(levels[i]);
            var mine = AtiCompress.Compress(ToBgra(RcMipChain.WriteRgba8(levels[i], srgb: false)),
                levels[i].Width, levels[i].Height, format, perceptual: false);
            Assert.True(file.AsSpan(at, mine.Length).SequenceEqual(mine), $"{vtexC}: mip {i} differs");
        }
    }

    [Theory]
    [InlineData("ati1n.vtex_c")]
    [InlineData("ati2n.vtex_c")]
    public void LinearFallback_MatchesResourceCompiler(string vtexC)
    {
        var (rgba, w, h) = LoadPng("probe.png");
        var levels = RcMipChain.Build(rgba, w, h, srgb: false);
        var mine = AtiCompress.Compress(ToBgra(RcMipChain.FallbackRgba8(levels, srgb: false, clamp: false)), 32, 32,
            AtiCompress.Format.Dxt5, perceptual: false);
        Assert.Equal(Header(vtexC).Fallback, mine);
    }

    // FALLBACK_BITS is the first extra-data payload; each entry's offset counts from its own field.
    private static (float[] Reflectivity, byte[] Fallback) Header(string vtexC)
    {
        var file = File.ReadAllBytes(Fixture(vtexC));
        int blockOffset = BitConverter.ToInt32(file, 8), blockCount = BitConverter.ToInt32(file, 12);
        int data = -1;
        for (int i = 0; i < blockCount; i++)
        {
            int p = 8 + blockOffset + i * 12;
            if (System.Text.Encoding.ASCII.GetString(file, p, 4) == "DATA")
                data = p + 4 + BitConverter.ToInt32(file, p + 4);
        }
        var reflectivity = Enumerable.Range(0, 4).Select(i => BitConverter.ToSingle(file, data + 4 + i * 4)).ToArray();
        int entry = data + 32 + BitConverter.ToInt32(file, data + 32);
        int payload = entry + 4 + BitConverter.ToInt32(file, entry + 4);
        return (reflectivity, file.AsSpan(payload, 1024).ToArray());
    }

    [Theory]
    [InlineData("probe.png", "rgba.vtex_c")]
    [InlineData("probe.png", "dxt1.vtex_c")]
    [InlineData("probe.png", "dxt5.vtex_c")]
    [InlineData("probe_npot.png", "dxt1_npot.vtex_c")]
    [InlineData("probe_7x5.png", "rgba_7x5.vtex_c")]
    [InlineData("probe_7x5.png", "dxt5_7x5.vtex_c")]
    [InlineData("probe_opaque.png", "dxt1_opaque.vtex_c")]
    public void Fallback_MatchesResourceCompiler(string png, string vtexC)
    {
        var (rgba, w, h) = LoadPng(png);
        var levels = RcMipChain.Build(rgba, w, h, srgb: true);
        var mine = AtiCompress.Compress(ToBgra(RcMipChain.FallbackRgba8(levels, srgb: true, clamp: false)), 32, 32,
            AtiCompress.Format.Dxt5, perceptual: true);
        Assert.Equal(Header(vtexC).Fallback, mine);
    }

    // Reflectivity is the 1x1 level, float for float.
    [Theory]
    [InlineData("probe.png", "rgba.vtex_c", true)]
    [InlineData("probe_npot.png", "rgba_npot.vtex_c", true)]
    [InlineData("probe_npot.png", "dxt1_npot.vtex_c", true)]
    [InlineData("probe_7x5.png", "rgba_7x5.vtex_c", true)]
    [InlineData("probe_opaque.png", "dxt1_opaque.vtex_c", true)]
    [InlineData("probe.png", "ati1n.vtex_c", false)]
    [InlineData("probe.png", "ati2n.vtex_c", false)]
    public void Reflectivity_IsTheOneByOneLevel(string png, string vtexC, bool srgb)
    {
        var (rgba, w, h) = LoadPng(png);
        var oneByOne = RcMipChain.Build(rgba, w, h, srgb)[^1].Rgba;
        Assert.Equal(Header(vtexC).Reflectivity.Select(BitConverter.SingleToInt32Bits),
            oneByOne.Select(BitConverter.SingleToInt32Bits));
    }
}
