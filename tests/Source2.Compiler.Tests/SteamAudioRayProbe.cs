using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <c>SARAYS=&lt;rays.bin&gt;;&lt;capture dir&gt;</c> (tools/steamaudio/capture_rays.py on a capture_phonon_api.py
/// capture): every valid lane of the captured Embree packets traced by brute force over the capture's mesh with
/// Embree 4.4's Möller tests (MoellerTrumboreIntersectorK, the packet one, and MoellerTrumboreIntersector1, the
/// single-ray one; FMA dot and cross, rcp = rcpps plus one Newton step), against Embree's answer. Measures how
/// often the two tests disagree and how often a lane has tied candidates, i.e. where the BVH's traversal order
/// would decide.
/// </summary>
public class SteamAudioRayProbe(ITestOutputHelper output)
{
    private static float Dot(float ax, float ay, float az, float bx, float by, float bz)
        => MathF.FusedMultiplyAdd(ax, bx, MathF.FusedMultiplyAdd(ay, by, az * bz));

    private static (float, float, float) Cross(float ax, float ay, float az, float bx, float by, float bz)
        => (MathF.FusedMultiplyAdd(ay, bz, -(az * by)), MathF.FusedMultiplyAdd(az, bx, -(ax * bz)), MathF.FusedMultiplyAdd(ax, by, -(ay * bx)));

    private static float Rcp(float a)
    {
        var r = Sse.ReciprocalScalar(Vector128.CreateScalar(a)).ToScalar();
        var h = MathF.FusedMultiplyAdd(-a, r, 1.0f);
        return MathF.FusedMultiplyAdd(r, h, r);
    }

    private static float Xor(float v, float sign) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) ^ BitConverter.SingleToInt32Bits(sign));

    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("SARAYS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var bytes = File.ReadAllBytes(parts[0]);
        var dir = parts[1];
        var mi = Directory.GetFiles(dir, "mesh_*_vertices.bin").Select(f => int.Parse(Path.GetFileName(f).Split('_')[1])).Max();
        var v = MemoryMarshalFloats(File.ReadAllBytes(Path.Combine(dir, $"mesh_{mi}_vertices.bin")));
        var t = MemoryMarshalInts(File.ReadAllBytes(Path.Combine(dir, $"mesh_{mi}_triangles.bin")));
        var nt = t.Length / 3;
        var tri = new (float v0x, float v0y, float v0z, float e1x, float e1y, float e1z, float e2x, float e2y, float e2z, float nx, float ny, float nz)[nt];
        for (var i = 0; i < nt; i++)
        {
            int a = t[3 * i], b = t[3 * i + 1], c = t[3 * i + 2];
            float v0x = v[3 * a], v0y = v[3 * a + 1], v0z = v[3 * a + 2];
            float e1x = v0x - v[3 * b], e1y = v0y - v[3 * b + 1], e1z = v0z - v[3 * b + 2];
            float e2x = v[3 * c] - v0x, e2y = v[3 * c + 1] - v0y, e2z = v[3 * c + 2] - v0z;
            var (nx, ny, nz) = Cross(e2x, e2y, e2z, e1x, e1y, e1z);
            tri[i] = (v0x, v0y, v0z, e1x, e1y, e1z, e2x, e2y, e2z, nx, ny, nz);
        }
        int lanes = 0, kExact = 0, oneExact = 0, ties = 0, kMiss = 0, formulasDiffer = 0, occl = 0, occlExact = 0;
        var pos = 0;
        var shown = 0;
        while (pos < bytes.Length)
        {
            var kind = bytes[pos];
            var size = 4 + 32 + (kind == 0 ? 2 * 640 : 2 * 384);
            var rec = bytes.AsSpan(pos, size);
            pos += size;
            var valid = MemoryMarshalInts(rec.Slice(4, 32).ToArray());
            var before = MemoryMarshalFloats(rec.Slice(36, kind == 0 ? 640 : 384).ToArray());
            var after = MemoryMarshalFloats(rec.Slice(36 + (kind == 0 ? 640 : 384), kind == 0 ? 640 : 384).ToArray());
            for (var l = 0; l < 8; l++)
            {
                if (valid[l] == 0)
                    continue;
                float F(float[] a, int field) => a[field * 8 + l];
                float ox = F(before, 0), oy = F(before, 1), oz = F(before, 2), tnear = F(before, 3);
                float dx = F(before, 4), dy = F(before, 5), dz = F(before, 6), tfar = F(before, 8);
                // candidates under each test: (t, prim, u, v)
                var bestK = (t: tfar, prim: -1, u: 0f, v: 0f, nx: 0f, ny: 0f, nz: 0f);
                var best1 = bestK;
                var tieK = false;
                for (var p = 0; p < nt; p++)
                {
                    var q = tri[p];
                    float cx = q.v0x - ox, cy = q.v0y - oy, cz = q.v0z - oz;
                    var (rx, ry, rz) = Cross(cx, cy, cz, dx, dy, dz);
                    var den = Dot(q.nx, q.ny, q.nz, dx, dy, dz);
                    var absDen = MathF.Abs(den);
                    var sgn = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(den) & unchecked((int)0x80000000));
                    var uK = Xor(Dot(q.e2x, q.e2y, q.e2z, rx, ry, rz), sgn);
                    var vK = Xor(Dot(q.e1x, q.e1y, q.e1z, rx, ry, rz), sgn);
                    var T = Xor(Dot(q.nx, q.ny, q.nz, cx, cy, cz), sgn);
                    var okK = uK >= 0 && vK >= 0 && absDen - uK - vK >= 0 && absDen * tnear < T && T <= absDen * bestK.t && den != 0;
                    var u1 = Xor(Dot(rx, ry, rz, q.e2x, q.e2y, q.e2z), sgn);
                    var v1 = Xor(Dot(rx, ry, rz, q.e1x, q.e1y, q.e1z), sgn);
                    var ok1 = den != 0 && u1 >= 0 && v1 >= 0 && u1 + v1 <= absDen && absDen * tnear < T && T <= absDen * best1.t;
                    var r = Rcp(absDen);
                    if (okK)
                    {
                        var tt = T * r;
                        if (tt == bestK.t && bestK.prim >= 0)
                            tieK = true;
                        bestK = (tt, p, uK * r, vK * r, q.nx, q.ny, q.nz);
                    }
                    if (ok1)
                        best1 = (T * r, p, u1 * r, v1 * r, q.nx, q.ny, q.nz);
                }
                if (kind == 1)
                {
                    occl++;
                    var occludedValve = float.IsNegativeInfinity(F(after, 8));
                    if (occludedValve == (bestK.prim >= 0))
                        occlExact++;
                    continue;
                }
                lanes++;
                var prim = BitConverter.SingleToInt32Bits(F(after, 12 + 5));
                bool Same((float t, int prim, float u, float v, float nx, float ny, float nz) b)
                    => b.prim == prim && b.t == F(after, 8) && b.u == F(after, 12 + 3) && b.v == F(after, 12 + 4)
                       && b.nx == F(after, 12) && b.ny == F(after, 13) && b.nz == F(after, 14);
                if (Same(bestK)) kExact++;
                else if (shown++ < 10)
                    output.WriteLine($"lane miss: valve prim {prim} t {F(after, 8):R} u {F(after, 15):R} v {F(after, 16):R}; K prim {bestK.prim} t {bestK.t:R} u {bestK.u:R} v {bestK.v:R}");
                if (Same(best1)) oneExact++;
                if (bestK.prim < 0 && prim != -1) kMiss++;
                if (bestK.prim != best1.prim) formulasDiffer++;
                if (tieK) ties++;
            }
        }
        output.WriteLine($"intersect lanes {lanes}: K test exact {kExact}, 1-ray test exact {oneExact}, K found nothing where Embree hit {kMiss}, tests pick different triangles {formulasDiffer}, tied t {ties}");
        output.WriteLine($"occluded lanes {occl}: agree {occlExact}");
    }

    private static float[] MemoryMarshalFloats(byte[] b) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b).ToArray();
    private static int[] MemoryMarshalInts(byte[] b) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(b).ToArray();
}
