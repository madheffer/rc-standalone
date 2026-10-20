using Source2.Compiler.Simulation;

namespace Source2.Compiler.RcTexture;

/// <summary>
/// resourcecompiler's BC6H encode with the hue-shift fixup (FloatBitMap_t::WriteToBufferBC6H_ispctexcomp_highest_quality_tweaked,
/// 180c46dd0), which a final-quality lightmap (hueShiftFixup) takes. Every block is encoded as is, then
/// tried again with its colours changed while its error stays at or above 1. A trial's error is the summed
/// squared CIE Lab distance between the image and the decoded block, both scaled by 0.5 over the brightest
/// clamped channel (at least 0.25). The trials, in order: unmodified; colour consistency over the whole
/// block at the minimum brightness 0, 0.005, 0.01, 0.015, 0.02, 0.025, 0.05, 0.075 and 0.1; colour
/// consistency per BC6H partition 0..31 at 0.01, 0.03 and 0.05; k-means quantisation to 15 down to 3
/// colours. A block takes a trial's encode when its error is lower than the best so far.
/// <para>Trials are batched: the queue holds max(block rows / 32, 256) rows of blocks and is encoded
/// (veryslow), decoded and scored when an enqueue finds it full and once at the end, so a trial sees the
/// errors of the batches flushed before it. A block is skipped when a pixel's r^2 + g^2 + b^2 is not below
/// 1e12, before or after its change.</para>
/// </summary>
public static class Bc6hHueShift
{
    private const float Third = 0.33333334f;

    /// <summary>The float a pixel channel becomes in the encoder's input (the row fill 180c3ef20): max(0, x), sign cleared, the truncating float16.</summary>
    public static ushort FillHalf(float x)
    {
        var m = float.IsNaN(x) ? x : MathF.Max(0f, x);
        return Maps.WorldNodeModels.ToHalf(BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(m) & 0x7fffffff));
    }

    // maxss a, b: a when a > b, else b (b on equality and NaN).
    private static float MaxSs(float a, float b) => a > b ? a : b;

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    /// <summary>181269b90 from 3 (linear RGB to XYZ).</summary>
    private static (float X, float Y, float Z) Xyz(float r, float g, float b)
        => (g * F(0x3eb71759) + r * F(0x3ed32618) + b * F(0x3e38d4fe),
            g * F(0x3f371759) + r * F(0x3e59b3d0) + b * F(0x3d93dd98),
            g * F(0x3df41f21) + r * F(0x3c9e1b09) + b * F(0x3f7353f8));

    /// <summary>181269b90 to 3 (XYZ to linear RGB, unclamped).</summary>
    private static (float R, float G, float B) Rgb(float x, float y, float z)
        => (x * F(0x404f65fe) - y * F(0x3fc4c2f8) - z * F(0x3eff4880),
            y * F(0x3ff01a37) - x * F(0x3f7809d5) + z * F(0x3d29fbe7),
            x * F(0x3d6425af) - y * F(0x3e50e560) + z * F(0x3f874bc7));

    /// <summary>181269b90 to 1 (XYZ to xyY): x and y over (Y + X) + Z, 0 when that is not above 0.</summary>
    private static (float X, float Y, float L) XyY(float x, float y, float z)
    {
        var s = y + x + z;
        var inv = s > 0f ? 1f / s : 0f;
        return (x * inv, y * inv, y);
    }

    /// <summary>181269b90 from 1 (xyY to XYZ): Y / y, 0 when y is not above 0.</summary>
    private static (float X, float Y, float Z) FromXyY(float x, float y, float l)
    {
        var s = y > 0f ? l / y : 0f;
        return (x * s, l, (1f - x - y) * s);
    }

    private static float LabF(float t)
        => t > F(0x3c111aa7) ? CrtMath.Pow(t, Third) : t / F(0x3e038028) + F(0x3e0d3dcb);

    /// <summary>181269b90 to 4 (XYZ to CIE Lab, D65; the cube root through tier0's powf).</summary>
    private static (float L, float A, float B) Lab(float x, float y, float z)
    {
        var fx = LabF(x * F(0x3c2c5fdb));
        var fy = LabF(y * 0.01f);
        var fz = LabF(z * F(0x3c1678d9));
        return (fy * 116f - 16f, (fx - fy) * 500f, (fy - fz) * 200f);
    }

    private static bool Valid(ReadOnlySpan<float> px)
    {
        for (var p = 0; p < 16; p++)
        {
            float r = px[p * 3], g = px[p * 3 + 1], b = px[p * 3 + 2];
            if (!(r * r + g * g + b * b < 1e12f))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The encode of <paramref name="rgba"/> (width x height x depth, RGBA floats, both sides multiples of 4) to
    /// BC6H blocks; <paramref name="encode"/> encodes a half RGBA image (the veryslow profile) to its blocks.
    /// </summary>
    public static byte[] Encode(float[] rgba, int width, int height, int depth, Func<Half[], int, int, byte[]> encode)
    {
        if (width % 4 != 0 || height % 4 != 0)
            throw new NotSupportedException("the hue-shift BC6H encode of an image whose sides are not multiples of 4 (180c46dd0 fails) is not ported");
        return new Run(rgba, width, height, depth, encode).Go();
    }

    private sealed class Run
    {
        private readonly float[] _img;
        private readonly int _w, _h, _bx, _by, _perZ, _total, _cap;
        private readonly Func<Half[], int, int, byte[]> _encode;
        private readonly float[] _best;
        private readonly byte[] _out;
        private readonly int[] _queue;
        private readonly float[] _slots;
        private int _count;
        private readonly KMeans _kmeans = new();

        public Run(float[] img, int w, int h, int d, Func<Half[], int, int, byte[]> encode)
        {
            (_img, _w, _h, _encode) = (img, w, h, encode);
            _bx = w / 4;
            _by = h / 4;
            _perZ = _bx * _by;
            _total = _perZ * d;
            _cap = Math.Max(_by >> 5, 256) * _bx;
            _best = new float[_total];
            Array.Fill(_best, float.MaxValue);
            _out = new byte[_total * 16];
            _queue = new int[_cap];
            _slots = new float[_cap * 48];
        }

        public byte[] Go()
        {
            Trial(null);
            foreach (var mb in new[] { 0f, F(0x3ba3d70a), F(0x3c23d70a), F(0x3c75c28f), F(0x3ca3d70a), F(0x3ccccccd), F(0x3d4ccccd), F(0x3d99999a), F(0x3dcccccd) })
                Trial(px => ColourConsistency(px, -1, mb));
            for (var p = 0; p < 32; p++)
            {
                var partition = p;
                foreach (var mb in new[] { F(0x3c23d70a), F(0x3cf5c28f), F(0x3d4ccccd) })
                    Trial(px => ColourConsistency(px, partition, mb));
            }
            for (var n = 15; n > 2; n--)
            {
                var clusters = n;
                Trial(px =>
                {
                    Quantize(px, clusters);
                    return true;
                });
            }
            Flush();
            return _out;
        }

        private void Gather(int block, Span<float> px)
        {
            var z = block / _perZ;
            var rem = block - z * _perZ;
            var y = rem / _bx;
            var x = rem - y * _bx;
            for (var py = 0; py < 4; py++)
                for (var pxi = 0; pxi < 4; pxi++)
                {
                    var i = ((z * _h + y * 4 + py) * _w + x * 4 + pxi) * 4;
                    var o = (py * 4 + pxi) * 3;
                    px[o] = _img[i];
                    px[o + 1] = _img[i + 1];
                    px[o + 2] = _img[i + 2];
                }
        }

        // 180c2f400 / 180c2ed30 / 180c2f890: z, then rows, then columns of blocks.
        private void Trial(Func<float[], bool>? change)
        {
            var px = new float[48];
            for (var block = 0; block < _total; block++)
            {
                if (1f > _best[block])
                    continue;
                Gather(block, px);
                if (!Valid(px))
                    continue;
                if (change is not null && !change(px))
                    continue;
                if (!Valid(px))
                    continue;
                if (_count == _cap)
                    Flush();
                _queue[_count] = block;
                px.CopyTo(_slots.AsSpan(_count * 48, 48));
                _count++;
            }
        }

        // 180c2ed30: every pixel's chromaticity set to its region's mean (weighted by Y), Y raised to the region's brightest times minBright.
        private static bool ColourConsistency(float[] px, int partition, float minBright)
        {
            Span<float> sx = stackalloc float[2], sy = stackalloc float[2], sl = stackalloc float[2], maxL = stackalloc float[2];
            for (var i = 0; i < 16; i++)
            {
                var sub = partition == -1 ? 0 : (Bc6hDecoder.Partitions[partition] >> i) & 1;
                var (X, Y, Z) = Xyz(px[i * 3], px[i * 3 + 1], px[i * 3 + 2]);
                var (x, y, l) = XyY(X, Y, Z);
                sx[sub] = x * l + sx[sub];
                sy[sub] = y * l + sy[sub];
                maxL[sub] = MaxSs(maxL[sub], l);
                sl[sub] = l + sl[sub];
            }
            if (sl[0] <= 0f && sl[1] <= 0f)
                return false;
            for (var s = 0; s < 2; s++)
            {
                var inv = 1f / MaxSs(sl[s], 1e-8f);
                sx[s] *= inv;
                sy[s] *= inv;
            }
            for (var i = 0; i < 16; i++)
            {
                var sub = partition == -1 ? 0 : (Bc6hDecoder.Partitions[partition] >> i) & 1;
                var (X, Y, Z) = Xyz(px[i * 3], px[i * 3 + 1], px[i * 3 + 2]);
                var l = XyY(X, Y, Z).L;
                var (nx, ny, nz) = FromXyY(sx[sub], sy[sub], MaxSs(l, maxL[sub] * minBright));
                (px[i * 3], px[i * 3 + 1], px[i * 3 + 2]) = Rgb(nx, ny, nz);
            }
            return true;
        }

        // 180c37720: k-means to n clusters, each pixel then its cluster's mean (summed in member order, times 1 / count).
        private void Quantize(float[] px, int n)
        {
            var members = _kmeans.Assign(px, n);
            foreach (var list in members)
            {
                if (list.Count == 0)
                    continue;
                float x = 0f, y = 0f, z = 0f;
                foreach (var m in list)
                {
                    x += px[m * 3];
                    y += px[m * 3 + 1];
                    z += px[m * 3 + 2];
                }
                var inv = 1f / list.Count;
                (x, y, z) = (x * inv, y * inv, z * inv);
                foreach (var m in list)
                    (px[m * 3], px[m * 3 + 1], px[m * 3 + 2]) = (x, y, z);
            }
        }

        // 180c37470: encode the queue's blocks, decode them, score each against the image (ProcessBC6HResults, 180c360c0).
        private void Flush()
        {
            if (_count == 0)
                return;
            var rows = (_count + _bx - 1) / _bx;
            int bw = _bx * 4, bh = rows * 4;
            var half = new Half[bw * bh * 4];
            for (var s = 0; s < _count; s++)
            {
                int sy = s / _bx, sxb = s - sy * _bx;
                for (var p = 0; p < 16; p++)
                {
                    var i = ((sy * 4 + p / 4) * bw + sxb * 4 + p % 4) * 4;
                    for (var c = 0; c < 3; c++)
                        half[i + c] = BitConverter.UInt16BitsToHalf(FillHalf(_slots[s * 48 + p * 3 + c]));
                    half[i + 3] = BitConverter.UInt16BitsToHalf(0x3c00);
                }
            }
            var encoded = _encode(half, bw, bh);
            var errors = new float[_count];
            Parallel.For(0, _count, s =>
            {
                Span<ushort> dec = stackalloc ushort[48];
                Span<float> orig = stackalloc float[48];
                Bc6hDecoder.DecodeBlock(encoded.AsSpan(s * 16, 16), dec);
                GatherSpan(_queue[s], orig);
                errors[s] = Error(orig, dec);
            });
            for (var s = 0; s < _count; s++)
            {
                var block = _queue[s];
                if (_best[block] > errors[s])
                {
                    _best[block] = errors[s];
                    encoded.AsSpan(s * 16, 16).CopyTo(_out.AsSpan(block * 16, 16));
                }
            }
            _count = 0;
        }

        private void GatherSpan(int block, Span<float> px)
        {
            var tmp = new float[48];
            Gather(block, tmp);
            tmp.CopyTo(px);
        }

        private static float Error(ReadOnlySpan<float> orig, ReadOnlySpan<ushort> dec)
        {
            var err = 0f;
            for (var p = 0; p < 16; p++)
            {
                var rc = MaxSs(0f, orig[p * 3]);
                var gc = MaxSs(0f, orig[p * 3 + 1]);
                var bc = MaxSs(0f, orig[p * 3 + 2]);
                var s = 0.5f / MaxSs(0.25f, MaxSs(rc, MaxSs(gc, bc)));
                var (ox, oy, oz) = Xyz(s * rc, s * gc, s * bc);
                var a = Lab(ox, oy, oz);
                float dr = (float)BitConverter.UInt16BitsToHalf(dec[p * 3]), dg = (float)BitConverter.UInt16BitsToHalf(dec[p * 3 + 1]),
                      db = (float)BitConverter.UInt16BitsToHalf(dec[p * 3 + 2]);
                var (dx, dy, dz) = Xyz(dr * s, dg * s, db * s);
                var b = Lab(dx, dy, dz);
                float d0 = a.L - b.L, d1 = a.A - b.A, d2 = a.B - b.B;
                err += d0 * d0 + d1 * d1 + d2 * d2;
            }
            return err;
        }
    }

    /// <summary>
    /// mathlib's CKMeansClustering (18126add0) over a block's 16 pixels (dimension 3), one thread. Centres start at
    /// distinct pixels picked by tier0's uniform random stream seeded 0x8a4d0191 (18126b2e0); each pixel joins its
    /// nearest centre (the first on a tie, 18126ab10); then, up to 64 rounds while a centre moves more than 0.001 or a
    /// cluster is empty: an empty cluster takes the last member of the largest (the first largest, 18126ba50), the
    /// centres become their members' means (18126bde0), and the pixels are assigned again.
    /// </summary>
    private sealed class KMeans
    {
        public List<int>[] Assign(float[] px, int n)
        {
            if (n == 16)
                throw new NotSupportedException("k-means with a cluster per pixel is not ported");
            var centres = new float[n][];
            var members = new List<int>[n];
            var rng = new Maps.SmartPropEvaluator.UniformRandomStream();
            rng.SetSeed(unchecked((int)0x8a4d0191));
            var picked = new HashSet<int>();
            for (var k = 0; k < n; k++)
            {
                int idx;
                do
                    idx = rng.RandomInt(0, 15);
                while (!picked.Add(idx));
                centres[k] = [px[idx * 3], px[idx * 3 + 1], px[idx * 3 + 2]];
                members[k] = [];
            }
            AssignAll(px, centres, members);
            for (var round = 0; round < 64; round++)
            {
                var empty = FixEmpty(px, centres, members);
                var moved = Recentre(px, centres, members);
                foreach (var m in members)
                    m.Clear();
                AssignAll(px, centres, members);
                var anyEmpty = members.Any(m => m.Count == 0);
                if (!(moved || empty) && !anyEmpty)
                    break;
            }
            return members;
        }

        private static void AssignAll(float[] px, float[][] centres, List<int>[] members)
        {
            for (var v = 0; v < 16; v++)
            {
                var best = float.MaxValue;
                var at = -1;
                for (var k = 0; k < centres.Length; k++)
                {
                    var d = 0f;
                    for (var c = 0; c < 3; c++)
                    {
                        var e = centres[k][c] - px[v * 3 + c];
                        d += e * e;
                    }
                    if (!(best <= d))
                    {
                        best = d;
                        at = k;
                    }
                }
                members[at].Add(v);
            }
        }

        private static bool FixEmpty(float[] px, float[][] centres, List<int>[] members)
        {
            var any = false;
            for (var i = 0; i < members.Length; i++)
            {
                if (members[i].Count != 0)
                    continue;
                any = true;
                var largest = 0;
                for (var k = 1; k < members.Length; k++)
                    if (!(members[k].Count <= members[largest].Count))
                        largest = k;
                var from = members[largest];
                var v = from[^1];
                centres[i] = [px[v * 3], px[v * 3 + 1], px[v * 3 + 2]];
                members[i].Add(v);
                from.RemoveAt(from.Count - 1);
            }
            return any;
        }

        private static bool Recentre(float[] px, float[][] centres, List<int>[] members)
        {
            var moved = false;
            for (var k = 0; k < members.Length; k++)
            {
                if (members[k].Count == 0)
                    continue;
                Span<float> sum = stackalloc float[3];
                foreach (var v in members[k])
                    for (var c = 0; c < 3; c++)
                        sum[c] = px[v * 3 + c] + sum[c];
                for (var c = 0; c < 3; c++)
                {
                    var mean = 1f / members[k].Count * sum[c];
                    if (!(MathF.Abs(centres[k][c] - mean) <= 0.001f))
                        moved = true;
                    centres[k][c] = mean;
                }
            }
            return moved;
        }
    }
}
