using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.SteamAudio;

/// <summary>An acoustic material: absorption and transmission per band (low, mid, high), scattering.</summary>
public record struct AcousticMaterial(float Absorption0, float Absorption1, float Absorption2, float Scattering,
                                      float Transmission0, float Transmission1, float Transmission2)
{
    public readonly float Absorption(int band) => band switch { 0 => Absorption0, 1 => Absorption1, _ => Absorption2 };
}

/// <summary>
/// A static triangle scene traced the way phonon's Embree 4.4 scene answers: BVH8Triangle4 with the Möller tests
/// (triangle_intersector_moeller.h, MoellerTrumboreIntersectorK): per triangle v0, e1 = v0 - v1, e2 = v2 - v0,
/// Ng = cross(e2, e1); dot and cross as FMA chains (vec3.h); a hit when U, V and W = absDen - U - V are not
/// negative, den != 0 and absDen tnear &lt; T &lt;= absDen tfar; t, u, v scaled by rcp(absDen) = rcpps plus one FMA
/// Newton step. Every triangle is tested (no BVH), keeping the last one that passes against the shrinking tfar,
/// so a hit equals Embree's unless two triangles give the same t, where Embree's traversal order would choose:
/// those are counted in <see cref="Ties"/> (SteamAudioRayProbe matched 75,874 captured lanes with none).
/// </summary>
public sealed class TriangleScene
{
    private readonly float[] _v0x, _v0y, _v0z, _e1x, _e1y, _e1z, _e2x, _e2y, _e2z, _nx, _ny, _nz;
    public readonly int[] MaterialIndices;
    public readonly AcousticMaterial[] Materials;
    public long Ties;
    /// <summary>The first ties seen: the ray and the two triangles (for diagnosis).</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> TieLog = new();

    public int Count => _v0x.Length;

    /// <summary>Vertices (x, y, z in metres) and triangles as the static mesh hands them to Embree.</summary>
    public TriangleScene(ReadOnlySpan<float> vertices, ReadOnlySpan<int> triangles, int[] materialIndices, AcousticMaterial[] materials)
    {
        var n = triangles.Length / 3;
        _v0x = new float[n]; _v0y = new float[n]; _v0z = new float[n];
        _e1x = new float[n]; _e1y = new float[n]; _e1z = new float[n];
        _e2x = new float[n]; _e2y = new float[n]; _e2z = new float[n];
        _nx = new float[n]; _ny = new float[n]; _nz = new float[n];
        for (var i = 0; i < n; i++)
        {
            int a = triangles[3 * i], b = triangles[3 * i + 1], c = triangles[3 * i + 2];
            float v0x = vertices[3 * a], v0y = vertices[3 * a + 1], v0z = vertices[3 * a + 2];
            _v0x[i] = v0x; _v0y[i] = v0y; _v0z[i] = v0z;
            _e1x[i] = v0x - vertices[3 * b]; _e1y[i] = v0y - vertices[3 * b + 1]; _e1z[i] = v0z - vertices[3 * b + 2];
            _e2x[i] = vertices[3 * c] - v0x; _e2y[i] = vertices[3 * c + 1] - v0y; _e2z[i] = vertices[3 * c + 2] - v0z;
            (_nx[i], _ny[i], _nz[i]) = Embree.Cross(_e2x[i], _e2y[i], _e2z[i], _e1x[i], _e1y[i], _e1z[i]);
        }
        MaterialIndices = materialIndices;
        Materials = materials;
    }

    public struct Hit
    {
        public int Prim;
        public float T, U, V, NgX, NgY, NgZ;
    }

    /// <summary>The closest hit in (tnear, tfar]; Prim -1 when none.</summary>
    public Hit Intersect(float ox, float oy, float oz, float dx, float dy, float dz, float tnear, float tfar)
    {
        var hit = new Hit { Prim = -1, T = tfar };
        for (var p = 0; p < _v0x.Length; p++)
        {
            if (!Test(p, ox, oy, oz, dx, dy, dz, tnear, hit.T, out var T, out var U, out var V, out var absDen))
                continue;
            var r = Embree.Rcp(absDen);
            var t = T * r;
            if (hit.Prim >= 0 && t == hit.T && Interlocked.Increment(ref Ties) <= 8)
                TieLog.Enqueue($"o ({ox:R}, {oy:R}, {oz:R}) d ({dx:R}, {dy:R}, {dz:R}) t {t:R}: prim {hit.Prim} (u {hit.U:R} v {hit.V:R}) then prim {p} (u {U * r:R} v {V * r:R})");
            hit = new Hit { Prim = p, T = t, U = U * r, V = V * r, NgX = _nx[p], NgY = _ny[p], NgZ = _nz[p] };
        }
        return hit;
    }

    /// <summary>
    /// rtcIntersect1 (MoellerTrumboreIntersector1: U + V &lt;= absDen) for phonon's IScene::closestHit (180afba80):
    /// the hit distance in (tnear, tfar], +inf when none.
    /// </summary>
    public float ClosestHit1(float ox, float oy, float oz, float dx, float dy, float dz, float tnear, float tfar)
    {
        var best = float.PositiveInfinity;
        var far = tfar;
        for (var p = 0; p < _v0x.Length; p++)
        {
            float cx = _v0x[p] - ox, cy = _v0y[p] - oy, cz = _v0z[p] - oz;
            var (rx, ry, rz) = Embree.Cross(cx, cy, cz, dx, dy, dz);
            var den = Embree.Dot(_nx[p], _ny[p], _nz[p], dx, dy, dz);
            var absDen = MathF.Abs(den);
            var sgn = BitConverter.SingleToInt32Bits(den) & unchecked((int)0x80000000);
            var u = Xor(Embree.Dot(rx, ry, rz, _e2x[p], _e2y[p], _e2z[p]), sgn);
            var v = Xor(Embree.Dot(rx, ry, rz, _e1x[p], _e1y[p], _e1z[p]), sgn);
            if (!(den != 0.0f && u >= 0.0f && v >= 0.0f && u + v <= absDen))
                continue;
            var T = Xor(Embree.Dot(_nx[p], _ny[p], _nz[p], cx, cy, cz), sgn);
            if (!(absDen * tnear < T && T <= absDen * far))
                continue;
            var t = T * Embree.Rcp(absDen);
            if (best != float.PositiveInfinity && t == best)
                Interlocked.Increment(ref Ties);
            best = t;
            far = t;
        }
        return best;
    }

    /// <summary>
    /// rtcOccluded1 (the single-ray MoellerTrumboreIntersector1 ClosestHit1 uses, with the occlusion epilog) for
    /// phonon's EmbreeScene::isOccluded (180afbc20): any triangle in (tnear, tfar].
    /// </summary>
    public bool Occluded1(float ox, float oy, float oz, float dx, float dy, float dz, float tnear, float tfar)
    {
        for (var p = 0; p < _v0x.Length; p++)
        {
            float cx = _v0x[p] - ox, cy = _v0y[p] - oy, cz = _v0z[p] - oz;
            var (rx, ry, rz) = Embree.Cross(cx, cy, cz, dx, dy, dz);
            var den = Embree.Dot(_nx[p], _ny[p], _nz[p], dx, dy, dz);
            var absDen = MathF.Abs(den);
            var sgn = BitConverter.SingleToInt32Bits(den) & unchecked((int)0x80000000);
            var u = Xor(Embree.Dot(rx, ry, rz, _e2x[p], _e2y[p], _e2z[p]), sgn);
            var v = Xor(Embree.Dot(rx, ry, rz, _e1x[p], _e1y[p], _e1z[p]), sgn);
            if (!(den != 0.0f && u >= 0.0f && v >= 0.0f && u + v <= absDen))
                continue;
            var T = Xor(Embree.Dot(_nx[p], _ny[p], _nz[p], cx, cy, cz), sgn);
            if (absDen * tnear < T && T <= absDen * tfar)
                return true;
        }
        return false;
    }

    /// <summary>rtcOccluded: any triangle in (tnear, tfar].</summary>
    public bool Occluded(float ox, float oy, float oz, float dx, float dy, float dz, float tnear, float tfar)
    {
        for (var p = 0; p < _v0x.Length; p++)
            if (Test(p, ox, oy, oz, dx, dy, dz, tnear, tfar, out _, out _, out _, out _))
                return true;
        return false;
    }

    private bool Test(int p, float ox, float oy, float oz, float dx, float dy, float dz, float tnear, float tfar,
                      out float T, out float U, out float V, out float absDen)
    {
        float cx = _v0x[p] - ox, cy = _v0y[p] - oy, cz = _v0z[p] - oz;
        var (rx, ry, rz) = Embree.Cross(cx, cy, cz, dx, dy, dz);
        var den = Embree.Dot(_nx[p], _ny[p], _nz[p], dx, dy, dz);
        absDen = MathF.Abs(den);
        var sgn = BitConverter.SingleToInt32Bits(den) & unchecked((int)0x80000000);
        U = Xor(Embree.Dot(_e2x[p], _e2y[p], _e2z[p], rx, ry, rz), sgn);
        V = 0;
        T = 0;
        if (!(U >= 0.0f))
            return false;
        V = Xor(Embree.Dot(_e1x[p], _e1y[p], _e1z[p], rx, ry, rz), sgn);
        if (!(V >= 0.0f))
            return false;
        if (!(absDen - U - V >= 0.0f))
            return false;
        T = Xor(Embree.Dot(_nx[p], _ny[p], _nz[p], cx, cy, cz), sgn);
        return absDen * tnear < T && T <= absDen * tfar && den != 0.0f;
    }

    private static float Xor(float v, int sign) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) ^ sign);
}

/// <summary>Embree 4.4's AVX2 vector maths (vec3.h, vfloat8_avx.h) on one lane.</summary>
internal static class Embree
{
    public static float Dot(float ax, float ay, float az, float bx, float by, float bz)
        => MathF.FusedMultiplyAdd(ax, bx, MathF.FusedMultiplyAdd(ay, by, az * bz));

    public static (float, float, float) Cross(float ax, float ay, float az, float bx, float by, float bz)
        => (MathF.FusedMultiplyAdd(ay, bz, -(az * by)), MathF.FusedMultiplyAdd(az, bx, -(ax * bz)), MathF.FusedMultiplyAdd(ax, by, -(ay * bx)));

    /// <summary>rcp: rcpps, then r + r (1 - a r) with FMA.</summary>
    public static float Rcp(float a)
    {
        var r = Sse.ReciprocalScalar(Vector128.CreateScalar(a)).ToScalar();
        return MathF.FusedMultiplyAdd(r, MathF.FusedMultiplyAdd(-a, r, 1.0f), r);
    }
}

/// <summary>
/// The ISPC math phonon 0923's AVX2 kernels use (read from 180201a50 and 1801f5290): rsqrt as rsqrtps with one
/// Newton step written (r (3 - r (x r))) * 0.5, and pow(x, y) = exp(y log(x)) with ISPC's own log (mantissa in
/// [0.5, 1), a degree 9 polynomial in 1 - m) and exp (floor(x log2 e), Cody-Waite ln 2, a degree 6 polynomial,
/// the exponent built into the float, overflow to inf and underflow to 0).
/// </summary>
internal static class Ispc
{
    private static float Fma(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);

    public static float Rsqrt(float x)
    {
        var r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(x)).ToScalar();
        var t = x * r;
        return r * Fma(-r, t, 3.0f) * 0.5f;
    }

    private static float Log(float x)
    {
        var nonPositive = !(0.0f < x);
        var negative = !(0.0f <= x);
        var xs = nonPositive ? 1.0f : x;
        var bits = BitConverter.SingleToInt32Bits(xs);
        var e = (bits >> 23) + unchecked((int)0xffffff82);
        var m = BitConverter.Int32BitsToSingle((bits & unchecked((int)0x807fffff)) | 0x3f000000);
        var t = 1.0f - m;
        var p = Fma(t, 1.7491014003753662f, -2.489927053451538f);
        p = Fma(t, p, 1.984423041343689f);
        p = Fma(t, p, -0.5996323227882385f);
        p = Fma(t, p, 0.34244194626808167f);
        p = Fma(t, p, 0.17541764676570892f);
        p = Fma(t, p, 0.2519190013408661f);
        p = Fma(t, p, 0.3332604169845581f);
        p = Fma(t, p, 0.5000009536743164f);
        p = Fma(p, t, 1.0f);
        p = t * p;
        var log = Fma(0.6931471824645996f, e, -p);
        return nonPositive ? (negative ? float.NaN : float.NegativeInfinity) : log;
    }

    private static float Exp(float y)
    {
        var kf = MathF.Floor(y * 1.4426950216293335f);
        var k = Cvtt(kf);
        var r = Fma(-0.693145751953125f, kf, y);
        r = Fma(-1.428606765330187e-06f, kf, r);
        var p = Fma(r, 0.00027555381529964507f, 0.0013043793151155114f);
        p = Fma(r, p, 0.00837883073836565f);
        p = Fma(r, p, 0.04165390506386757f);
        p = Fma(r, p, 0.16666841506958008f);
        p = Fma(r, p, 0.49999991059303284f);
        p = Fma(p, r, 1.0f);
        p = Fma(p, r, 1.0f);
        var biased = unchecked(k + 127);
        var v = p * BitConverter.Int32BitsToSingle(biased << 23);
        if (k > 127)
            v = float.PositiveInfinity;
        if (biased < 1)
            v = 0.0f;
        return v;
    }

    /// <summary>cvttps2dq: truncation, int.MinValue for NaN and out-of-range values.</summary>
    public static int Cvtt(float f) => float.IsNaN(f) || f >= 2147483648.0f || f < -2147483648.0f ? int.MinValue : (int)f;

    public static float Pow(float x, float y) => Exp(Log(x) * y);
}

/// <summary>
/// phonon 0923's reverb bake, ported: EmbreeReflectionSimulator::simulate's jobs of 64 listener rays (one per
/// listener sample), each running the AVX2 simulateEnergyField kernel (180201a50) in gangs of 8 lanes, then the
/// last job adding every thread's field into the output (180afeff0, EnergyField::add). One thread, as the only
/// reproducible case. The kernel, from its disassembly (4.9 differs from the public 4.8.1 ISPC):
/// <list type="bullet">
/// <item>the seed of each job's Embree RandomSampler is rdtsc in Valve's build; here <see cref="Seed"/>, mixed
/// as RandomSampler_init does (MurmurHash3 mix into 0, finalize); draws are an LCG (1664525, 1013904223), (s >> 1)
/// as int times 2^-31;</item>
/// <item>a lane stops on a miss, when its accumulated distance exceeds duration x speed of sound, and on bounce 0
/// when the hit is nearer than the listener radius 0.1 (only bounce 0 in 4.9); there are no source or listener
/// sphere tests after bounce 0 and no irradiance-distance cut;</item>
/// <item>the normal is Ng normalized (fma(z, z, fma(x, x, y y)), rsqrt), flipped to face the ray; the hit point
/// fma(0.01, n, fma(d, t, o));</item>
/// <item>shade: hitToSource; skip when dot(n, hitToSource) &lt; 0; distance = sqrt; shadow ray to the source
/// (occluded skips); diffuse (1/pi) s max(dot(n, l), 0) and specular 4.0584507 (1 - s) pow(dot(h, n), 100) with
/// h = normalized((l - d) 0.5) (left out when the simulator's byte at 0x68 is set); attenuation 1 / max(distance,
/// irradiance min distance); the term ((att^2 / (4 pi)) scalar) directivity (diffuse + specular); per band
/// accum (term (1 - absorption)); delay ((accum + t) + distance - |source - listener|) / speed;</item>
/// <item>bin = floor(delay / binDuration); per band, per channel, lanes in order: data = fma(e, coeff, data);</item>
/// <item>bounce (not after the last): accum *= 1 - absorption, distance += t; one draw u for the gang, diffuse
/// lanes where scattering &gt; u; if any, a second draw picks the diffuse sample floor(u (n - 1)) clamped; the
/// diffuse direction through createCoordinateSpace(n) and transformLocalToWorld, the others reflected.</item>
/// </list>
/// </summary>
public sealed class ReflectionSimulator
{
    public const float SpeedOfSound = 340.0f;
    public const int RayBatch = 64;
    private const float FltMax = 3.4028234663852886e+38f;

    public int Seed;
    public float BinDuration = 0.01f;
    public float IrradianceMinDistance = 1.0f;
    public bool NoSpecular;

    private readonly float[][] _listener, _diffuse, _coeffs;

    public ReflectionSimulator(int maxRays, int numDiffuseSamples, int maxOrder)
    {
        _listener = Sampling.Sphere(maxRays);
        _diffuse = Sampling.Hemisphere(numDiffuseSamples);
        _coeffs = Sampling.Coefficients(_listener, maxOrder);
    }

    /// <summary>The source's coordinate space: right, up, ahead and origin, as ispcCoordinateSpace hands them.</summary>
    public readonly record struct Space(float RightX, float RightY, float RightZ, float UpX, float UpY, float UpZ,
                                        float AheadX, float AheadY, float AheadZ, float X, float Y, float Z);

    private static uint Murmur(uint seed)
    {
        var k = unchecked((uint)seed * 0xcc9e2d51);
        k = (k << 15) | (k >> 17);
        k = unchecked(k * 0x1b873593);
        var h = 0u ^ k;
        h = unchecked(((h << 13) | (h >> 19)) * 5 + 0xe6546b64);
        h ^= h >> 16;
        h = unchecked(h * 0x85ebca6b);
        h ^= h >> 13;
        h = unchecked(h * 0xc2b2ae35);
        h ^= h >> 16;
        return h;
    }

    private static float Draw(ref uint s)
    {
        s = unchecked(s * 1664525u + 1013904223u);
        return (int)(s >> 1) * 4.656612873077392578125e-10f;
    }

    /// <summary>
    /// One reverb probe (source = listener = the probe centre, identity directivity): the energy field as
    /// [channel][band][bin], zeroed and filled by numRays / 64 jobs in order.
    /// </summary>
    public float[][][] Bake(TriangleScene scene, Space listener, Space source, int numRays, int numBounces, float duration,
                            int order, int numBins, float dipoleWeight = 0, float dipolePower = 0)
    {
        const int bands = 3;
        var channels = (order + 1) * (order + 1);
        var field = new float[channels][][];
        for (var c = 0; c < channels; c++)
        {
            field[c] = new float[bands][];
            for (var b = 0; b < bands; b++)
                field[c][b] = new float[numBins];
        }
        var scalar = 12.566370964050293f / numRays;
        var sl = (source.X - listener.X, source.Y - listener.Y, source.Z - listener.Z);
        var slx = sl.Item1 * sl.Item1; var sly = sl.Item2 * sl.Item2; var slz = sl.Item3 * sl.Item3;
        var sourceListener = MathF.Sqrt(slz + (slx + sly));
        var maxDistance = duration * SpeedOfSound;
        for (var start = 0; start < numRays; start += RayBatch)
        {
            var rng = Murmur(unchecked((uint)Seed));
            var end = Math.Min(start + RayBatch, numRays);
            if ((end - start) % 8 != 0)
                throw new NotSupportedException("a partial ISPC gang (rays not a multiple of 8) is not ported");
            for (var g = start; g < end; g += 8)
                Gang(scene, listener, source, g, numBounces, maxDistance, scalar, sourceListener, field, ref rng,
                     dipoleWeight, dipolePower, numBins);
        }
        return field;
    }

    private void Gang(TriangleScene scene, Space listener, Space source, int first, int numBounces, float maxDistance,
                      float scalar, float sourceListener, float[][][] field, ref uint rng, float dipoleWeight,
                      float dipolePower, int numBins)
    {
        const int L = 8, bands = 3;
        var channels = field.Length;
        Span<float> ox = stackalloc float[L], oy = stackalloc float[L], oz = stackalloc float[L];
        Span<float> dx = stackalloc float[L], dy = stackalloc float[L], dz = stackalloc float[L];
        Span<float> accumDistance = stackalloc float[L];
        Span<float> accum = stackalloc float[L * bands];
        Span<bool> alive = stackalloc bool[L];
        for (var l = 0; l < L; l++)
        {
            ox[l] = listener.X; oy[l] = listener.Y; oz[l] = listener.Z;
            dx[l] = _listener[0][first + l]; dy[l] = _listener[1][first + l]; dz[l] = _listener[2][first + l];
            accumDistance[l] = 0;
            alive[l] = true;
            for (var b = 0; b < bands; b++)
                accum[l * bands + b] = 1.0f;
        }
        Span<float> nx = stackalloc float[L], ny = stackalloc float[L], nz = stackalloc float[L];
        Span<float> px = stackalloc float[L], py = stackalloc float[L], pz = stackalloc float[L];
        Span<float> tfar = stackalloc float[L];
        Span<int> material = stackalloc int[L];
        Span<float> energy = stackalloc float[L * bands];
        Span<int> bin = stackalloc int[L];
        Span<bool> lit = stackalloc bool[L];
        for (var bounce = 0; bounce < numBounces; bounce++)
        {
            var any = false;
            for (var l = 0; l < L; l++)
                any |= alive[l];
            if (!any)
                break;
            for (var l = 0; l < L; l++)
            {
                if (!alive[l])
                    continue;
                var hit = scene.Intersect(ox[l], oy[l], oz[l], dx[l], dy[l], dz[l], 0.0f, FltMax);
                if (hit.Prim < 0 || accumDistance[l] > maxDistance || (bounce == 0 && hit.T < 0.1f))
                {
                    alive[l] = false;
                    continue;
                }
                tfar[l] = hit.T;
                var rs = Ispc.Rsqrt(MathF.FusedMultiplyAdd(hit.NgZ, hit.NgZ, MathF.FusedMultiplyAdd(hit.NgX, hit.NgX, hit.NgY * hit.NgY)));
                float x = hit.NgX * rs, y = hit.NgY * rs, z = hit.NgZ * rs;
                if (MathF.FusedMultiplyAdd(dz[l], z, MathF.FusedMultiplyAdd(dx[l], x, dy[l] * y)) > 0.0f)
                {
                    x = -x; y = -y; z = -z;
                }
                nx[l] = x; ny[l] = y; nz[l] = z;
                px[l] = MathF.FusedMultiplyAdd(0.01f, x, MathF.FusedMultiplyAdd(dx[l], hit.T, ox[l]));
                py[l] = MathF.FusedMultiplyAdd(0.01f, y, MathF.FusedMultiplyAdd(dy[l], hit.T, oy[l]));
                pz[l] = MathF.FusedMultiplyAdd(0.01f, z, MathF.FusedMultiplyAdd(dz[l], hit.T, oz[l]));
                material[l] = scene.MaterialIndices[hit.Prim];
            }
            // Every lane stopped at the trace: the kernel jumps past the shading and the bounce (no draws).
            var hitAny = false;
            for (var l = 0; l < L; l++)
                hitAny |= alive[l];
            if (!hitAny)
                break;

            // shade (one source)
            for (var l = 0; l < L; l++)
            {
                lit[l] = false;
                if (!alive[l])
                    continue;
                float hx = source.X - px[l], hy = source.Y - py[l], hz = source.Z - pz[l];
                if (!(0.0f <= MathF.FusedMultiplyAdd(hz, nz[l], MathF.FusedMultiplyAdd(hx, nx[l], hy * ny[l]))))
                    continue;
                var distance = MathF.Sqrt(MathF.FusedMultiplyAdd(hz, hz, MathF.FusedMultiplyAdd(hx, hx, hy * hy)));
                float lx = hx / distance, ly = hy / distance, lz = hz / distance;
                if (scene.Occluded(px[l], py[l], pz[l], lx, ly, lz, 0.0f, distance))
                    continue;
                var m = scene.Materials[material[l]];
                var specular = 0.0f;
                if (!NoSpecular)
                {
                    float vx = (lx - dx[l]) * 0.5f, vy = (ly - dy[l]) * 0.5f, vz = (lz - dz[l]) * 0.5f;
                    var hrs = Ispc.Rsqrt(MathF.FusedMultiplyAdd(vz, vz, MathF.FusedMultiplyAdd(vx, vx, vy * vy)));
                    float hhx = vx * hrs, hhy = vy * hrs, hhz = vz * hrs;
                    var hn = MathF.FusedMultiplyAdd(hhz, nz[l], MathF.FusedMultiplyAdd(hhx, nx[l], hhy * ny[l]));
                    specular = (1.0f - m.Scattering) * 4.058450698852539f * Ispc.Pow(hn, 100.0f);
                }
                var directivity = Directivity(px[l], py[l], pz[l], source, dipoleWeight, dipolePower);
                var ln = MathF.FusedMultiplyAdd(lz, nz[l], MathF.FusedMultiplyAdd(lx, nx[l], ly * ny[l]));
                var diffuse = 0.31830987334251404f * m.Scattering * MathF.Max(ln, 0.0f);
                var attenuation = 1.0f / MathF.Max(distance, IrradianceMinDistance);
                var term = (diffuse + specular) * (directivity * (attenuation * attenuation * 0.07957746833562851f * scalar));
                for (var b = 0; b < bands; b++)
                    energy[l * bands + b] = accum[l * bands + b] * (term * (1.0f - m.Absorption(b)));
                var delay = (distance + (accumDistance[l] + tfar[l]) - sourceListener) / SpeedOfSound;
                var k = Ispc.Cvtt(MathF.Floor(delay / BinDuration));
                if (k < 0 || k >= numBins)
                    continue;
                bin[l] = k;
                lit[l] = true;
            }
            for (var b = 0; b < bands; b++)
                for (var c = 0; c < channels; c++)
                {
                    var row = field[c][b];
                    for (var l = 0; l < L; l++)
                        if (lit[l])
                            row[bin[l]] = MathF.FusedMultiplyAdd(energy[l * bands + b], _coeffs[c][first + l], row[bin[l]]);
                }

            if (bounce >= numBounces - 1)
                continue;
            for (var l = 0; l < L; l++)
            {
                if (!alive[l])
                    continue;
                var m = scene.Materials[material[l]];
                for (var b = 0; b < bands; b++)
                    accum[l * bands + b] *= 1.0f - m.Absorption(b);
            }
            var u = Draw(ref rng);
            var anyDiffuse = false;
            Span<bool> diffuseLane = stackalloc bool[L];
            for (var l = 0; l < L; l++)
            {
                diffuseLane[l] = alive[l] && !(scene.Materials[material[l]].Scattering <= u);
                anyDiffuse |= diffuseLane[l];
            }
            float sx = 0, sy = 0, sz = 0;
            if (anyDiffuse)
            {
                var n = _diffuse[0].Length;
                var v = Draw(ref rng) * (float)(n - 1);
                var index = Ispc.Cvtt(MathF.Floor(v));
                index = Math.Min(n - 1, index & ~(index >> 31));
                sx = _diffuse[0][index]; sy = _diffuse[1][index]; sz = _diffuse[2][index];
            }
            for (var l = 0; l < L; l++)
            {
                if (!alive[l])
                    continue;
                accumDistance[l] += tfar[l];
                float x, y, z;
                if (diffuseLane[l])
                {
                    float rx, ry, rz;
                    if (MathF.Abs(nx[l]) > MathF.Abs(nz[l]))
                    {
                        var rs = Ispc.Rsqrt(MathF.FusedMultiplyAdd(ny[l], ny[l], nx[l] * nx[l]) + 0.0f);
                        rx = -(ny[l] * rs); ry = nx[l] * rs; rz = rs * 0.0f;
                    }
                    else
                    {
                        var rs = Ispc.Rsqrt(MathF.FusedMultiplyAdd(ny[l], ny[l], MathF.FusedMultiplyAdd(nz[l], nz[l], 0.0f)));
                        rx = rs * 0.0f; ry = -(nz[l] * rs); rz = ny[l] * rs;
                    }
                    var ux = MathF.FusedMultiplyAdd(ry, nz[l], -(rz * ny[l]));
                    var uy = MathF.FusedMultiplyAdd(rz, nx[l], -(rx * nz[l]));
                    var uz = MathF.FusedMultiplyAdd(rx, ny[l], -(ry * nx[l]));
                    x = MathF.FusedMultiplyAdd(-sz, nx[l], MathF.FusedMultiplyAdd(sx, rx, sy * ux));
                    y = MathF.FusedMultiplyAdd(-sz, ny[l], MathF.FusedMultiplyAdd(sx, ry, sy * uy));
                    z = MathF.FusedMultiplyAdd(-sz, nz[l], MathF.FusedMultiplyAdd(sy, uz, sx * rz));
                }
                else
                {
                    var d2 = MathF.FusedMultiplyAdd(nz[l], dz[l], MathF.FusedMultiplyAdd(nx[l], dx[l], ny[l] * dy[l]));
                    d2 += d2;
                    x = MathF.FusedMultiplyAdd(-nx[l], d2, dx[l]);
                    y = MathF.FusedMultiplyAdd(-ny[l], d2, dy[l]);
                    z = MathF.FusedMultiplyAdd(-d2, nz[l], dz[l]);
                }
                var nrs = Ispc.Rsqrt(MathF.FusedMultiplyAdd(z, z, MathF.FusedMultiplyAdd(x, x, y * y)));
                dx[l] = x * nrs; dy[l] = y * nrs; dz[l] = z * nrs;
                ox[l] = px[l]; oy[l] = py[l]; oz[l] = pz[l];
            }
        }
    }

    /// <summary>evaluateDirectivity without a callback (1801f5290): pow(|(1 - w) - w localZ|, power).</summary>
    private static float Directivity(float x, float y, float z, Space s, float w, float power)
    {
        float vx = x - s.X, vy = y - s.Y, vz = z - s.Z;
        var rs = Ispc.Rsqrt(MathF.FusedMultiplyAdd(vz, vz, MathF.FusedMultiplyAdd(vx, vx, vy * vy)));
        vx *= rs; vy *= rs; vz *= rs;
        var localZ = -(vz * s.AheadZ) - MathF.FusedMultiplyAdd(vx, s.AheadX, vy * s.AheadY);
        var b = MathF.FusedMultiplyAdd(-w, localZ, 1.0f - w);
        return Ispc.Pow(MathF.Abs(b), power);
    }
}
