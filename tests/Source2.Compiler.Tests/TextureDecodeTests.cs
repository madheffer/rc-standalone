using SkiaSharp;
using Source2.Compiler.Textures;
using Source2.Compiler.Textures.Decoding;
using ValvePak;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Compiled textures decode to pixels without ValveResourceFormat, as its GenerateBitmap decodes them
/// (every pak01 texture agreed pixel for pixel when this was written; docs/TEXTURES.md, "Decoding").
/// </summary>
public class TextureDecodeTests
{
    private static byte[] Build(ResourceBuilder.TextureCompression compression, byte[] rgba, int width, int height, bool mips = false)
        => ResourceBuilder.BuildTexture(new ResourceBuilder.TextureDef
        {
            RawRgba = rgba,
            RawWidth = width,
            RawHeight = height,
            Compression = compression,
            GenerateMipmaps = mips,
        });

    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a);
        return rgba;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(SKBitmap bitmap, int x, int y)
    {
        var p = bitmap.GetPixelSpan().Slice((y * bitmap.Width + x) * 4, 4);
        return (p[2], p[1], p[0], p[3]);
    }

    [Fact]
    public void Uncompressed_DecodesToItsOwnPixels()
    {
        var rgba = Enumerable.Range(0, 8 * 4 * 4).Select(i => (byte)(i * 37)).ToArray();
        var texture = CompiledTexture.Read(Build(ResourceBuilder.TextureCompression.None, rgba, 8, 4));
        Assert.Equal(VTexFormat.BGRA8888, texture.Format);
        using var bitmap = texture.GenerateBitmap();
        Assert.Equal((8, 4, SKColorType.Bgra8888), (bitmap.Width, bitmap.Height, bitmap.ColorType));
        for (var i = 0; i < 8 * 4; i++)
            Assert.Equal((rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]), Pixel(bitmap, i % 8, i / 8));
    }

    [Theory]
    [InlineData(ResourceBuilder.TextureCompression.BC1, VTexFormat.DXT1)]
    [InlineData(ResourceBuilder.TextureCompression.BC3, VTexFormat.DXT5)]
    [InlineData(ResourceBuilder.TextureCompression.BC7, VTexFormat.BC7)]
    public void BlockFormats_KeepASolidColour(ResourceBuilder.TextureCompression compression, VTexFormat format)
    {
        var texture = CompiledTexture.Read(Build(compression, Solid(16, 8, 255, 0, 255, 255), 16, 8, mips: true));
        Assert.Equal(format, texture.Format);
        Assert.True(texture.NumMipLevels > 1);
        for (uint mip = 0; mip < texture.NumMipLevels; mip++)
        {
            using var bitmap = texture.GenerateBitmap(mipLevel: mip);
            Assert.Equal((Math.Max(16 >> (int)mip, 1), Math.Max(8 >> (int)mip, 1)), (bitmap.Width, bitmap.Height));
            // Magenta is exact in BC1/BC3's 565 endpoints; the BC7 encoder may land a step away.
            var (r, g, b, a) = Pixel(bitmap, bitmap.Width - 1, bitmap.Height - 1);
            Assert.True(r >= 254 && g <= 1 && b >= 254 && a == 255, $"{format} mip {mip}: {r},{g},{b},{a}");
            if (format != VTexFormat.BC7)
                Assert.Equal(((byte)255, (byte)0, (byte)255, (byte)255), (r, g, b, a));
        }
    }

    [Fact]
    public void Bc4_IsGrey_AndBc5_IsRedGreen()
    {
        using var bc4 = CompiledTexture.Read(Build(ResourceBuilder.TextureCompression.BC4, Solid(8, 8, 200, 0, 0, 255), 8, 8)).GenerateBitmap();
        Assert.Equal(((byte)200, (byte)200, (byte)200, (byte)255), Pixel(bc4, 3, 3));
        using var bc5 = CompiledTexture.Read(Build(ResourceBuilder.TextureCompression.BC5, Solid(8, 8, 200, 40, 0, 255), 8, 8))
            .GenerateBitmap(codec: TextureCodec.None);
        Assert.Equal(((byte)200, (byte)40, (byte)0, (byte)255), Pixel(bc5, 3, 3));
    }

    [Fact]
    public void Bc1_InterpolatesOnTheStoredBits()
    {
        // Red 1 and red 0 in 5 bits; every pixel takes the colour two thirds of the way to red 1:
        // 2/3 * 255/31 = 5.48 rounds to 5, where interpolating the expanded 8 and 0 would give 6 (or 5 truncated).
        byte[] block = [0x00, 0x08, 0x00, 0x00, 0xAA, 0xAA, 0xAA, 0xAA];
        var rgba = new byte[64];
        BlockDecoders.Decode(block, 4, 4, rgba, 4, 4, BlockFormat.Bc1NoAlpha);
        Assert.Equal([5, 0, 0, 255], rgba[..4]);
    }

    [Fact]
    public void HostileHeader_IsInvalidData()
    {
        var bytes = Build(ResourceBuilder.TextureCompression.None, Solid(4, 4, 1, 2, 3, 4), 4, 4);
        var texture = CompiledTexture.Read(bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => texture.GenerateBitmap(mipLevel: 5));
        Assert.Throws<ArgumentException>(() => texture.GenerateBitmap(face: CubemapFace.NegativeX));

        var file = ResourceFile.Read(bytes);
        var data = file["DATA"]!.Data;
        BitConverter.GetBytes((ushort)20000).CopyTo(data, 20);   // width past the limit
        Assert.Throws<InvalidDataException>(() => CompiledTexture.Read(file.Write()));

        var truncated = bytes[..^8];
        Assert.Throws<InvalidDataException>(() => CompiledTexture.Read(truncated).GenerateBitmap());
    }

    [Fact]
    public void StockTextures_DecodeInEveryStoredFormat()
    {
        if (CS2Fixtures.StockPak() is not { } path)
            return;
        using var pak = new Package();
        pak.Read(path);
        var seen = new HashSet<VTexFormat>();
        foreach (var entry in Io.VpkEntries.ByExtension(pak)["vtex_c"].OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            if (entry.TotalLength > 1 << 20)
                continue;
            pak.ReadEntry(entry, out var bytes);
            var texture = CompiledTexture.Read(bytes);
            if (!Enum.IsDefined(texture.Format) || !seen.Add(texture.Format))
                continue;
            using var bitmap = texture.GenerateBitmap(mipLevel: (uint)(texture.NumMipLevels - 1));
            Assert.True(bitmap.Width > 0 && bitmap.Height > 0, entry.GetFullPath());
            if (texture.IsHighDynamicRange)
            {
                Assert.Equal(SKColorType.RgbaF32, bitmap.ColorType);
                using var ldr = texture.GenerateBitmap(mipLevel: (uint)(texture.NumMipLevels - 1), codec: TextureCodec.ForceLDR);
                Assert.Equal(SKColorType.Bgra8888, ldr.ColorType);
            }
            if (seen.Count == 10)
                break;
        }
        Assert.True(seen.Count >= 5);
    }

    [Fact]
    public void StockSpriteSheets_ReadAndWriteBackToTheirBytes()
    {
        if (CS2Fixtures.StockPak() is not { } path)
            return;
        using var pak = new Package();
        pak.Read(path);
        var sheets = 0;
        foreach (var entry in Io.VpkEntries.ByExtension(pak)["vtex_c"].Where(e => e.GetFullPath().StartsWith("materials/particle/", StringComparison.Ordinal))
            .OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            pak.ReadEntry(entry, out var bytes);
            if (!CompiledTexture.Read(bytes).ExtraData.TryGetValue(VTexExtraData.SHEET, out var sheet))
                continue;
            var sequences = SpriteSheet.Read(sheet);
            Assert.NotEmpty(sequences);
            Assert.Equal(sheet, SpriteSheet.Write(sequences));
            if (++sheets == 50)
                break;
        }
        Assert.True(sheets > 0);
    }
}
