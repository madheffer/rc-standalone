using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch probe (TANGENTDECODE=normalhex,normalhex,normalhex|tangenthex x4):
/// every R32 frame whose decoded normal has those exact bits, with the
/// decoded tangent nearest the given one and how far it is in ulps.
/// </summary>
public class TangentDecodeProbe(ITestOutputHelper output)
{
    private static uint B(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    [Fact]
    public void Nearest()
    {
        if (Environment.GetEnvironmentVariable("TANGENTDECODE") is not { Length: > 0 } spec)
            return;
        foreach (var item in spec.Split(';'))
        {
            var p = item.Split('|');
            var n = p[0].Split(',').Select(h => Convert.ToUInt32(h, 16)).ToArray();
            var t = p[1].Split(',').Select(h => Convert.ToUInt32(h, 16)).ToArray();
            var found = false;
            for (uint uv = 0; uv < (1u << 20); uv++)
            {
                var (dn, _) = PackedNormals.DecodeR32(uv << 12);
                if (B(dn.X) != n[0] || B(dn.Y) != n[1] || B(dn.Z) != n[2])
                    continue;
                found = true;
                var best = (Dist: long.MaxValue, Q: 0u, T: default(System.Numerics.Vector4));
                for (uint q = 0; q < 2048; q++)
                {
                    var (_, dt) = PackedNormals.DecodeR32((uv << 12) | (q << 1) | (t[3] >> 31 == 0 ? 1u : 0u));
                    long d = Math.Abs((long)(int)B(dt.X) - (int)t[0]) + Math.Abs((long)(int)B(dt.Y) - (int)t[1]) + Math.Abs((long)(int)B(dt.Z) - (int)t[2]);
                    if (d < best.Dist)
                        best = (d, q, dt);
                }
                output.WriteLine($"normal code {uv:x5}: nearest q {best.Q} ulps {best.Dist}: {B(best.T.X):x8},{B(best.T.Y):x8},{B(best.T.Z):x8} against {t[0]:x8},{t[1]:x8},{t[2]:x8}");
                var rn = TJunctionFix.Normalise(dn);
                var rt = TJunctionFix.Normalise(new System.Numerics.Vector3(best.T.X, best.T.Y, best.T.Z));
                output.WriteLine($"  renormalised: n {B(rn.X):x8},{B(rn.Y):x8},{B(rn.Z):x8} t {B(rt.X):x8},{B(rt.Y):x8},{B(rt.Z):x8}");
            }
            if (!found)
            {
                var near = (Dist: long.MaxValue, Code: 0u, N: default(System.Numerics.Vector3));
                for (uint uv = 0; uv < (1u << 20); uv++)
                {
                    var (dn, _) = PackedNormals.DecodeR32(uv << 12);
                    long d = Math.Abs((long)(int)B(dn.X) - (int)n[0]) + Math.Abs((long)(int)B(dn.Y) - (int)n[1]) + Math.Abs((long)(int)B(dn.Z) - (int)n[2]);
                    if (d < near.Dist)
                        near = (d, uv, dn);
                }
                output.WriteLine($"no normal code decodes to {p[0]}; nearest {near.Code:x5} at {near.Dist} ulps: {B(near.N.X):x8},{B(near.N.Y):x8},{B(near.N.Z):x8} ({near.N.X}, {near.N.Y}, {near.N.Z})");
                var rn2 = TJunctionFix.Normalise(near.N);
                output.WriteLine($"  renormalised: n {B(rn2.X):x8},{B(rn2.Y):x8},{B(rn2.Z):x8}");
                for (uint q = 0; q < 2048; q += 2047)
                {
                    var (_, dt) = PackedNormals.DecodeR32((near.Code << 12) | (q << 1));
                    var rt2 = TJunctionFix.Normalise(new System.Numerics.Vector3(dt.X, dt.Y, dt.Z));
                    output.WriteLine($"  q {q}: t {B(dt.X):x8},{B(dt.Y):x8},{B(dt.Z):x8} renormalised {B(rt2.X):x8},{B(rt2.Y):x8},{B(rt2.Z):x8}");
                }
            }
        }
    }
}
