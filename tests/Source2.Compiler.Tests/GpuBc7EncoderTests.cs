using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins the GPU BC7 seam: a texture that asks for the GPU encoder reaches the one a host installed,
/// mip by mip, the cache names that encoder by its tag, and with none installed the CPU encodes.
/// </summary>
[Collection(nameof(GpuBc7EncoderTests))]
public sealed class GpuBc7EncoderTests : IDisposable
{
    public void Dispose() => GpuBc7Encoder.Use(null);

    private static byte[] Build(ResourceBuilder.Bc7EncoderMode mode) => ResourceBuilder.BuildTexture(new ResourceBuilder.TextureDef
    {
        RawRgba = Enumerable.Range(0, 16 * 16 * 4).Select(i => (byte)(i * 7)).ToArray(),
        RawWidth = 16,
        RawHeight = 16,
        Compression = ResourceBuilder.TextureCompression.BC7,
        Bc7Mode = mode,
    });

    [Fact]
    public void An_installed_encoder_encodes_every_mip_and_names_the_cache_entry()
    {
        var calls = new List<(int W, int H)>();
        GpuBc7Encoder.Use((bgra, w, h) =>
        {
            Assert.Equal(w * h * 4, bgra.Length);
            calls.Add((w, h));
            return Enumerable.Repeat((byte)0xA5, ((w + 3) / 4) * ((h + 3) / 4) * 16).ToArray();
        }, "fakegpu1");

        var vtex = Build(ResourceBuilder.Bc7EncoderMode.Gpu);

        Assert.Equal([(16, 16), (8, 8), (4, 4)], calls);
        Assert.Equal("fakegpu1", ResourceBuilder.Bc7CacheTag(ResourceBuilder.Bc7EncoderMode.Gpu));
        using var res = new Resource();
        res.Read(new MemoryStream(vtex));
        var tex = (Texture)res.DataBlock!;
        var mip0 = new byte[tex.CalculateBufferSizeForMipLevel(0)];
        tex.ReadTextureMipLevel(mip0, 0);
        Assert.All(mip0, b => Assert.Equal(0xA5, b));
    }

    [Fact]
    public void Without_one_the_cpu_encodes_and_the_cache_says_so()
    {
        GpuBc7Encoder.Use((_, _, _) => throw new InvalidOperationException("must not be called"));
        GpuBc7Encoder.Use(null);

        Assert.False(GpuBc7Encoder.Available);
        Assert.Equal(Build(ResourceBuilder.Bc7EncoderMode.Cpu), Build(ResourceBuilder.Bc7EncoderMode.Gpu));
        Assert.Equal(ResourceBuilder.Bc7CacheTag(ResourceBuilder.Bc7EncoderMode.Cpu),
            ResourceBuilder.Bc7CacheTag(ResourceBuilder.Bc7EncoderMode.Gpu));
    }

    [Theory]
    [InlineData("")]
    [InlineData("seventeen-chars!!")]
    [InlineData("has space")]
    public void A_cache_tag_must_be_short_and_alphanumeric(string tag)
        => Assert.Throws<ArgumentException>(() => GpuBc7Encoder.Use((_, _, _) => [], tag));
}
