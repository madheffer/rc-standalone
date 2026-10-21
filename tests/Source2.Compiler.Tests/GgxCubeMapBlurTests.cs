using System.Buffers.Binary;
using Source2.Compiler.Cubemaps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The GGX cubemap blur's tap tables against resourcecompiler's own, emulated by
/// tools/re/emu_cubeblur_tables.py into %TEMP%/cubeblur_tables.bin (records: kind 0 GGX / 1 cosine,
/// size, roughness, the 64 KB table); CUBEBLUR_TABLES=&lt;file&gt; reads another.
/// </summary>
public sealed class GgxCubeMapBlurTests(ITestOutputHelper output)
{
    [Fact]
    public void TablesAreResourceCompilers()
    {
        var path = Environment.GetEnvironmentVariable("CUBEBLUR_TABLES") ?? Path.Combine(Path.GetTempPath(), "cubeblur_tables.bin");
        if (!File.Exists(path))
            return;
        var b = File.ReadAllBytes(path);
        int total = 0, differ = 0;
        for (var at = 0; at + 12 + GgxCubeMapBlur.TableBytes <= b.Length; at += 12 + GgxCubeMapBlur.TableBytes)
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
            var size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 4));
            var roughness = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8));
            var want = b.AsSpan(at + 12, GgxCubeMapBlur.TableBytes);
            var got = kind == 1 ? GgxCubeMapBlur.CosineTable(size) : GgxCubeMapBlur.GgxTable(size, roughness);
            total++;
            if (want.SequenceEqual(got))
                continue;
            if (differ++ < 8)
            {
                var first = 0;
                while (first < got.Length && got[first] == want[first])
                    first++;
                var word = first / 4 * 4;
                output.WriteLine($"{(kind == 1 ? "cosine" : "ggx")} size {size} roughness {roughness:R}: first difference at float {word / 4} "
                    + $"(entry {word % 0x8000 / 16}, {(word >= 0x8000 ? "weight" : "tap")} component {word % 16 / 4}): "
                    + $"ours {BinaryPrimitives.ReadSingleLittleEndian(got.AsSpan(word)):R}, rc {BinaryPrimitives.ReadSingleLittleEndian(want[word..]):R}");
            }
        }
        output.WriteLine($"{total - differ} of {total} tables equal");
        Assert.True(total > 0);
        Assert.Equal(0, differ);
    }

    [Fact]
    public void Schedule()
    {
        // resourcecompiler's flags 3 on a 128 cube (a full chain of 8): GGX on mips 1 to 4 at (mip / 4)^2,
        // cosine on 5 to 7.
        Assert.Equal(8, GgxCubeMapBlur.MipCount(128));
        Assert.Equal((false, 0.0625f), GgxCubeMapBlur.Schedule(1, 8));
        Assert.Equal((false, 0.25f), GgxCubeMapBlur.Schedule(2, 8));
        Assert.Equal((false, 0.5625f), GgxCubeMapBlur.Schedule(3, 8));
        Assert.Equal((false, 1f), GgxCubeMapBlur.Schedule(4, 8));
        for (var mip = 5; mip < 8; mip++)
            Assert.True(GgxCubeMapBlur.Schedule(mip, 8).Cosine);
    }
}
