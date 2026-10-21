using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The CPU half of resourcecompiler's GGXCubeMapBlur mip algorithm
/// (CTextureFrame::GenerateMips_GGXCubeMapBlur, rc 0923 180150130 / 1015 1801502b0), the filter
/// env_cubemap_array.vtex compiles with. For each cubemap of the array, the six input faces go up as an
/// RGBA32F cube with a full mip chain made on the GPU; the destination's mip 0 is the input as it is,
/// and each mip from 1 is drawn face by face with materials/dev/convolve_environment_map.vmat (its
/// layer, rc 0923 1800caf90): a quad over the face, its direction turned by the face's basis, summing
/// <see cref="SampleCount"/> taps of the source cube (MinMagLinearMipPoint at the tap's lod) times the
/// tap's weight. What the GPU is handed per mip is built here: the taps and weights
/// (<see cref="Table"/>), the face bases (<see cref="FaceBasis"/>) and the quad (<see cref="Quad"/>).
/// Read from the disassembly (docs/CUBEMAPS.md).
/// </summary>
public static class GgxCubeMapBlur
{
    /// <summary>The taps per mip: the shader's g_nSampleCount and the table's capacity.</summary>
    public const int SampleCount = 0x800;

    /// <summary>The table's bytes: SampleCount float4 taps, then SampleCount float4 weights (x only).</summary>
    public const int TableBytes = SampleCount * 32;

    /// <summary>The flags resourcecompiler passes the layer (GenerateMips_GGXCubeMapBlur, rc 0923 180150c46).</summary>
    public const int TextureCompilerFlags = 3;

    /// <summary>
    /// A face's basis, the vertex shader's g_vMatRow0..2 (rc 0923 1827685f0, 0x24 bytes a face). The
    /// quad's direction (1, y, -x) dotted with each row gives the face's direction.
    /// </summary>
    public static readonly Vector3[][] FaceBasis =
    [
        [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)],
        [new(-1, 0, 0), new(0, 1, 0), new(0, 0, -1)],
        [new(0, 0, -1), new(1, 0, 0), new(0, -1, 0)],
        [new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0)],
        [new(0, 0, -1), new(0, 1, 0), new(1, 0, 0)],
        [new(0, 0, 1), new(0, 1, 0), new(-1, 0, 0)],
    ];

    /// <summary>
    /// The quad (rc 0923 1800cb081 .. 1800cb1c5): four 0x30-byte vertices drawn as a strip, position
    /// (x, y, 0) at 0 and direction (1, y, -x) at 20; the rest of each vertex is never written.
    /// </summary>
    public static readonly (Vector2 Position, Vector3 Direction)[] Quad =
    [
        (new(-1, -1), new(1, -1, 1)),
        (new(1, -1), new(1, -1, -1)),
        (new(-1, 1), new(1, 1, 1)),
        (new(1, 1), new(1, 1, -1)),
    ];

    /// <summary>
    /// The mips the layer schedules over: the destination cube's full chain down to 1 x 1 (rc 0923
    /// 180c49440 on the face size), however many the compiled texture keeps (128: 8, of which 6 ship).
    /// </summary>
    public static int MipCount(int size)
    {
        var count = 1;
        for (; size > 1; size >>= 1)
            count++;
        return count;
    }

    /// <summary>
    /// Whether mip <paramref name="mip"/> of <paramref name="mipCount"/> (<see cref="MipCount"/>) takes the cosine table, and
    /// the roughness the GGX table is built with otherwise (rc 0923 1800cb511 .. 1800cb6fe). With flag 1
    /// the last three mips are cosine and the rest divide by mipCount - 4, else by mipCount - 1; flag 2
    /// squares the ratio.
    /// </summary>
    public static (bool Cosine, float Roughness) Schedule(int mip, int mipCount, int flags = TextureCompilerFlags)
    {
        if ((flags & 1) != 0 && mip >= mipCount - 3)
            return (true, 0f);
        var denominator = (flags & 1) != 0 ? mipCount - 4 : mipCount - 1;
        var r = (float)mip / denominator;
        if ((flags & 2) != 0)
            r *= r;
        return (false, r);
    }

    /// <summary>The table for one mip of a cube <paramref name="size"/> texels on a side.</summary>
    public static byte[] Table(int mip, int mipCount, int size, int flags = TextureCompilerFlags)
    {
        var (cosine, roughness) = Schedule(mip, mipCount, flags);
        return cosine ? CosineTable(size) : GgxTable(size, roughness);
    }

    /// <summary>
    /// The GGX taps (rc 0923 1800caa20). The table is cleared; then a binary search over the
    /// Hammersley count n in (0x800, 0x4000) keeps the largest n whose reflected directions with
    /// 2c^2 - 1 above 0 number at most 0x800 (c the half vector's cosine). Each such direction goes in
    /// with its source lod, 0.5 log2((2pi / (D c 2048)) / (4pi / (6 size^2))) + 1 (at least 0), and its
    /// weight 2c^2 - 1; the weights are then scaled by one over their sum.
    /// </summary>
    public static byte[] GgxTable(int size, float roughness)
    {
        var table = new byte[TableBytes];
        var alpha = roughness * roughness;
        var alpha2m1 = alpha * alpha - 1f;
        uint hi = 0x4000, lo = 0x800;
        do
        {
            var n = (hi + lo) >> 1;
            uint kept = 0;
            if (n != 0)
            {
                var nf = (float)n;
                for (uint i = 0; i < n; i++)
                {
                    var t = (float)i / nf;
                    var c = Sqrt((1f - t) / (alpha2m1 * t + 1f));
                    // The loop also takes the sine and cosine of the azimuth, unused.
                    if (!(0f >= (c + c) * c - 1f))
                        kept++;
                }
                if (kept > 0x800)
                    hi = n;
                else
                    lo = n;
            }
            else
                lo = n;
        }
        while (hi - lo > 1);

        var count = lo;
        var lodScale = 12.566371f / (int)(size * size * 3 * 2);
        var a2 = alpha * alpha;
        var a2m1 = a2 - 1f;
        var sum = 0f;
        var stored = 0;
        var countF = (float)count;
        for (uint i = 0; i < count; i++)
        {
            var t = (float)i / countF;
            var c = Sqrt((1f - t) / (t * a2m1 + 1f));
            var s = Sqrt(1f - c * c);
            var (sinPhi, cosPhi) = SinCos((float)(RadicalInverse(i) * 2.3283064365386963e-10) * 6.2831855f);
            var twoC = c + c;
            var nDotL = c * twoC - 1f;
            var x = cosPhi * s * twoC - 0f;
            var y = sinPhi * s * twoC - 0f;
            if (0f >= nDotL)
                continue;
            var cm = MaxSs(0f, c);
            var d = cm * a2m1 * cm + 1f;
            var pdf = a2 * cm / (d * 3.1415927f * d) * 2048f;
            var lod = Log2(6.2831855f / pdf / lodScale) * 0.5f + 1f;
            Put(table, stored, x, y, nDotL, MaxSs(lod, 0f));
            BinaryPrimitives.WriteSingleLittleEndian(table.AsSpan(0x8000 + stored * 16), nDotL);
            sum += nDotL;
            stored++;
        }
        var inverse = 1f / sum;
        for (var i = 0; i < stored; i++)
        {
            var at = table.AsSpan(0x8000 + i * 16);
            BinaryPrimitives.WriteSingleLittleEndian(at, inverse * BinaryPrimitives.ReadSingleLittleEndian(at));
        }
        return table;
    }

    /// <summary>
    /// The cosine taps the last mips take (inline in the layer, rc 0923 1800cb526 .. 1800cb6cc): for
    /// each of the 0x800 Hammersley points (u the radical inverse, phi = i / 2048 times 2pi), the
    /// direction (sqrt(u) cos phi, sqrt(u) sin phi, sqrt(1 - u)), lod 0.5 log2((2pi / (c / pi 2048)) /
    /// (4pi / (6 size^2))) + 1 (at least 0) and weight 1 / 2048.
    /// </summary>
    public static byte[] CosineTable(int size)
    {
        var table = new byte[TableBytes];
        var lodScale = 12.566371f / (int)(size * size * 3 * 2);
        for (uint i = 0; i < SampleCount; i++)
        {
            var u = (float)(RadicalInverse(i) * 2.3283064365386963e-10);
            var r = Sqrt(u);
            var (sinPhi, cosPhi) = SinCos((float)i * 0.00048828125f * 6.2831855f);
            var c = Sqrt(MaxSs(0f, 1f - u));
            var y = sinPhi * r;
            var x = r * cosPhi;
            var lod = Log2(6.2831855f / (c / 3.1415927f * 2048f) / lodScale) * 0.5f + 1f;
            Put(table, (int)i, x, y, c, MaxSs(lod, 0f));
            BinaryPrimitives.WriteSingleLittleEndian(table.AsSpan(0x8000 + (int)i * 16), 0.00048828125f);
        }
        return table;
    }

    /// <summary>The 32-bit bit reversal the tables take as Hammersley's second coordinate (before the 2^-32 scale).</summary>
    public static uint RadicalInverse(uint i)
    {
        var v = i >> 16 | i << 16;
        v = v >> 1 & 0x55555555 | (v & 0xd5555555) * 2;
        v = (v & 0xf3333333) << 2 | v >> 2 & 0x33333333;
        v = v >> 4 & 0x0f0f0f0f | (v & 0xff0f0f0f) << 4;
        return v >> 8 & 0x00ff00ff | (v & 0xffff00ff) << 8;
    }

    private static void Put(byte[] table, int index, float x, float y, float z, float w)
    {
        var at = table.AsSpan(index * 16);
        BinaryPrimitives.WriteSingleLittleEndian(at, x);
        BinaryPrimitives.WriteSingleLittleEndian(at[4..], y);
        BinaryPrimitives.WriteSingleLittleEndian(at[8..], z);
        BinaryPrimitives.WriteSingleLittleEndian(at[12..], w);
    }

    // maxss a, b: a when a > b, else b (so b for a NaN).
    private static float MaxSs(float a, float b) => a > b ? a : b;

    // sqrtss; the binary's negative branch calls the CRT's sqrtf, which gives the same NaN.
    private static float Sqrt(float x) => MathF.Sqrt(x);

    // tier0's V_sincosf: the CRT's sinf and cosf.
    private static (float Sin, float Cos) SinCos(float x) => (CrtMath.Sin(x), CrtMath.Cos(x));

    // tier0's V_log2f.
    private static float Log2(float x) => CrtMath.Log2(x);
}
