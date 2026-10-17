namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The random numbers vrad3's ray tracing shaders read (g_RandomSamples):
/// scenesystem's rtx_random_samples.vtex, a 32 x 32 x 128 RG32F volume that
/// rtx_generate_scrambled_halton_samples fills before every pass (2026-10-15
/// scenesystem 180187920, the ISceneUtils slot vrad3's block compute calls at
/// 0x1a0 with the block's pass counter). It is PBRT-v3's HaltonSampler over a
/// 32 x 32 tile:
/// <list type="bullet">
/// <item>base scales 2^5 = 32 and 3^4 = 81 (the smallest powers reaching
/// 32), sample stride 2592, and the multiplicative inverses (extended GCD,
/// 180183050) making each texel's first index ((rev2(x) 81 inv(81, 32) +
/// inv3(y) 32 inv(32, 81)) mod 2592), plus (frame + 1) 2592;</item>
/// <item>texel (x, y, z) holds dimensions 2z and 2z + 1: dimension 0 the
/// plain base-2 radical inverse, every other dimension a scrambled radical
/// inverse in its prime through textures/dev/scrambled_halton.vdata's
/// uint16 permutation table, a few digits per lookup
/// (<see cref="Groups"/>, read from the shader).</item>
/// </list>
/// </summary>
internal static partial class HaltonSamples
{
    public const int Size = 32, Layers = 128;

    private const uint Stride = 2592, Mult0 = 81 * 17, Mult1 = 32 * 38;

    /// <summary>The volume for one pass: float pairs, index ((z Size + y) Size + x) 2.</summary>
    public static float[] Generate(ReadOnlySpan<ushort> permutations, uint frame)
    {
        var samples = new float[Size * Size * Layers * 2];
        for (var y = 0u; y < Size; y++)
            for (var x = 0u; x < Size; x++)
            {
                var index = Index(x, y, frame);
                for (var z = 0; z < Layers; z++)
                {
                    var at = ((z * Size + (int)y) * Size + (int)x) * 2;
                    samples[at] = Dimension(permutations, 2 * z, index);
                    samples[at + 1] = Dimension(permutations, 2 * z + 1, index);
                }
            }
        return samples;
    }

    /// <summary>The shader's sample index for texel (x, y) of a pass.</summary>
    public static uint Index(uint x, uint y, uint frame)
    {
        x &= Size - 1;
        y &= Size - 1;
        var rev = ReverseBits(x) >> (32 - 5);
        var inv3 = 0u;
        var t = y;
        for (var d = 0; d < 4; d++)
        {
            inv3 = inv3 * 3 + t % 3;
            t /= 3;
        }
        return (uint)(((ulong)rev * Mult0 + (ulong)inv3 * Mult1) % Stride) + (frame + 1) * Stride;
    }

    public static float Dimension(ReadOnlySpan<ushort> permutations, int dimension, uint index)
    {
        if (dimension == 0)
            return BitConverter.Int32BitsToSingle(0x3f800000 | (int)(ReverseBits(index) >> 9)) - 1f;
        var groups = Groups[dimension];
        var mults = Multipliers[dimension];
        var sum = 0u;
        for (var g = 0; g < groups.Length; g++)
        {
            var (offset, div, mod) = groups[g];
            sum += permutations[offset + (int)(index / div % mod)] * mults[g];
        }
        return sum * Scales[dimension];
    }

    private static uint ReverseBits(uint v)
    {
        v = (v << 16) | (v >> 16);
        v = ((v & 0x00ff00ff) << 8) | ((v & 0xff00ff00) >> 8);
        v = ((v & 0x0f0f0f0f) << 4) | ((v & 0xf0f0f0f0) >> 4);
        v = ((v & 0x33333333) << 2) | ((v & 0xcccccccc) >> 2);
        return ((v & 0x55555555) << 1) | ((v & 0xaaaaaaaa) >> 1);
    }

    /// <summary>scrambled_halton.vdata's permutations as uint16s (the buffer is read two to a uint32, low half first).</summary>
    public static ushort[] Permutations(byte[] blob)
    {
        var p = new ushort[blob.Length / 2];
        for (var i = 0; i < p.Length; i++)
            p[i] = BitConverter.ToUInt16(blob, i * 2);
        return p;
    }
}
