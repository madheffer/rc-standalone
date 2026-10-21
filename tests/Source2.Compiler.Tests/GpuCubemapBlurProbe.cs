using System.Buffers.Binary;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Gpu;
using Source2.Compiler.RcTexture;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// A sanity check of the cubemap blur and radiance SH on a shipped env_cubemap_array.vtex_c, with no
/// oracle: each cube's mip 0 decoded from BC6H stands in for its EXR, filtered by
/// <see cref="GpuCubemapBlur"/>, and the result compared with the shipped mips (also decoded); the SH of
/// the decoded mip 0 is compared with the shipped CUBEMAP_RADIANCE_SH floats. Both differ by BC6H's
/// rounding of the input only, so a wrong face, axis or table shows as a large error.
/// <c>CUBEPROBE=&lt;env_cubemap_array.vtex_c&gt;[|cubes]</c> (default 2 cubes).
/// </summary>
public sealed class GpuCubemapBlurProbe(ITestOutputHelper output)
{
    [Fact]
    public void AgainstShippedArray()
    {
        if (Environment.GetEnvironmentVariable("CUBEPROBE") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var file = File.ReadAllBytes(parts[0]);
        var cubesToCheck = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 2;
        var (data, dataEnd) = Block(file, "DATA");
        var size = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        var format = data[26];
        var mips = data[27];
        Assert.Equal(19, format);
        output.WriteLine($"{size} x {size}, {depth} cubes, {mips} mips");

        // Payload: mips smallest first; within a mip, cube by cube, face by face.
        var levelOffset = new int[mips];
        var at = dataEnd;
        for (var mip = mips - 1; mip >= 0; mip--)
        {
            levelOffset[mip] = at;
            var side = Math.Max(4, size >> mip);
            at += side / 4 * (side / 4) * 16 * 6 * depth;
        }
        Assert.Equal(file.Length, at);

        float[] Face(int mip, int cube, int face)
        {
            var side = Math.Max(4, size >> mip);
            var blocks = side / 4;
            var start = levelOffset[mip] + ((cube * 6 + face) * blocks * blocks) * 16;
            var rgba = new float[side * side * 4];
            Span<ushort> px = stackalloc ushort[48];
            for (var by = 0; by < blocks; by++)
                for (var bx = 0; bx < blocks; bx++)
                {
                    Bc6hDecoder.DecodeBlock(file.AsSpan(start + (by * blocks + bx) * 16, 16), px);
                    for (var i = 0; i < 16; i++)
                    {
                        var o = ((by * 4 + i / 4) * side + bx * 4 + i % 4) * 4;
                        for (var c = 0; c < 3; c++)
                            rgba[o + c] = (float)BitConverter.UInt16BitsToHalf(px[i * 3 + c]);
                        rgba[o + 3] = 1f;
                    }
                }
            return rgba;
        }

        var sh = ShippedSh(data);
        var stock = CS2Fixtures.StockPak() ?? throw new InvalidOperationException("no CS2");
        var shaderVpk = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stock)!, "..", "core", "shaders_vulkan_dir.vpk"));
        using var blur = new GpuCubemapBlur(shaderVpk);
        output.WriteLine($"device {blur.DeviceName}");
        for (var cube = 0; cube < Math.Min(cubesToCheck, depth); cube++)
        {
            var faces = Enumerable.Range(0, 6).Select(f => Face(0, cube, f)).ToArray();
            var ours = blur.Filter(faces, size);
            for (var mip = 1; mip < mips; mip++)
            {
                double err = 0, mag = 0, worst = 0;
                var worstFace = -1;
                for (var face = 0; face < 6; face++)
                {
                    var theirs = Face(mip, cube, face);
                    double fe = 0, fm = 0;
                    for (var i = 0; i < theirs.Length; i++)
                    {
                        if (i % 4 == 3)
                            continue;
                        fe += Math.Abs(ours[mip][face][i] - theirs[i]);
                        fm += Math.Abs(theirs[i]);
                    }
                    err += fe;
                    mag += fm;
                    if (fe / Math.Max(1e-9, fm) > worst)
                        (worst, worstFace) = (fe / Math.Max(1e-9, fm), face);
                }
                output.WriteLine($"cube {cube} mip {mip}: mean relative error {err / Math.Max(1e-9, mag):P2}, worst face {worstFace} {worst:P2}");
            }
            var planes = faces.Select(f => (Channel(f, 0), Channel(f, 1), Channel(f, 2))).ToList();
            var ourSh = CubemapRadianceSh.Project(planes, size);
            double se = 0, sm = 0;
            for (var i = 0; i < CubemapRadianceSh.FloatsPerCube; i++)
            {
                se += Math.Abs(ourSh[i] - sh[cube * 27 + i]);
                sm += Math.Abs(sh[cube * 27 + i]);
            }
            output.WriteLine($"cube {cube} SH: relative error {se / Math.Max(1e-9, sm):P2}; ours {string.Join(" ", ourSh.Take(4).Select(x => x.ToString("G5")))}, shipped {string.Join(" ", sh.Skip(cube * 27).Take(4).Select(x => x.ToString("G5")))}");
        }
    }

    private static float[] Channel(float[] rgba, int c)
    {
        var plane = new float[rgba.Length / 4];
        for (var i = 0; i < plane.Length; i++)
            plane[i] = rgba[i * 4 + c];
        return plane;
    }

    // The CUBEMAP_RADIANCE_SH extra block: offset 8 and count from its own start, then the floats.
    private static float[] ShippedSh(ReadOnlySpan<byte> data)
    {
        var extraOffset = BinaryPrimitives.ReadInt32LittleEndian(data[32..]);
        var extraCount = BinaryPrimitives.ReadInt32LittleEndian(data[36..]);
        for (var e = 0; e < extraCount; e++)
        {
            var entry = 32 + extraOffset + e * 12;
            var type = BinaryPrimitives.ReadInt32LittleEndian(data[entry..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(data[(entry + 4)..]);
            if (type != 5)
                continue;
            var blob = entry + 4 + offset;
            var floatsAt = blob + BinaryPrimitives.ReadInt32LittleEndian(data[blob..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(data[(blob + 4)..]);
            var sh = new float[count];
            for (var i = 0; i < count; i++)
                sh[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(floatsAt + i * 4)..]);
            return sh;
        }
        throw new InvalidDataException("no CUBEMAP_RADIANCE_SH");
    }

    private static (byte[] Data, int End) Block(byte[] file, string type)
    {
        var blockOffset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        for (var i = 0; i < count; i++)
        {
            var at = 8 + blockOffset + i * 12;
            if (System.Text.Encoding.ASCII.GetString(file, at, 4) != type)
                continue;
            var offset = at + 4 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 8));
            return (file.AsSpan(offset, size).ToArray(), offset + size);
        }
        throw new InvalidDataException($"no {type}");
    }
}
