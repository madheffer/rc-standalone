using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>AddJumps' cylinder sweep against vphysics2.dll's own functions.</summary>
public sealed unsafe class NavJumpSweepOracleTests(ITestOutputHelper output)
{
    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    /// <summary>tier0's V_cosf and V_sinf at the cylinder's 16 angles against CrtMath.</summary>
    [Fact]
    public void TheCylinderAnglesMatchTier0()
    {
        if (Vphysics2Oracle.Load() is null)
            return;
        var cos = (delegate* unmanaged<float, float>)Vphysics2Oracle.Tier0("V_cosf");
        var sin = (delegate* unmanaged<float, float>)Vphysics2Oracle.Tier0("V_sinf");
        var angle = 0f;
        for (var k = 0; k < 16; k++)
        {
            Assert.Equal(Bits(cos(angle)), Bits(SteamAudio.CrtMath.Cos(angle)));
            Assert.Equal(Bits(sin(angle)), Bits(SteamAudio.CrtMath.Sin(angle)));
            angle += BitConverter.Int32BitsToSingle(0x3ec90fdb);
        }
    }

    /// <summary>RnHullCreateCylinder (1801ad370) to the bit: AddJumps' upright cylinders and random axes.</summary>
    [Fact]
    public void TheCylinderMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var create = (delegate* unmanaged<Vector3*, Vector3*, float, void*, byte*>)NativeLibrary.GetExport(module, "RnHullCreateCylinder");
        var random = new Random(26);
        var same = 0;
        const int trials = 400;
        for (var t = 0; t < trials; t++)
        {
            Vector3 a, b;
            if (t < 200)
            {
                // AddJumps' kinds: (0, 0, climb) to (0, 0, height / 2 + climb).
                var climb = F(random, 0, 40);
                a = new Vector3(0, 0, climb);
                b = new Vector3(0, 0, F(random, 1, 80) + climb);
            }
            else
            {
                a = new Vector3(F(random, -100, 100), F(random, -100, 100), F(random, -100, 100));
                b = a + new Vector3(F(random, -60, 60), F(random, -60, 60), F(random, 0.5, 60));
            }
            var radius = F(random, 4, 40);
            var valve = create(&a, &b, radius, null);
            var ours = RnHullBuilder.CreateCylinder(a, b, radius);
            var diff = Compare(valve, ours);
            if (diff is null)
                same++;
            else if (same + 10 > t)
                output.WriteLine($"trial {t} ({a:R} {b:R} {radius:R}): {diff}");
        }
        output.WriteLine($"{same} of {trials} cylinders Valve's");
        Assert.Equal(trials, same);
    }

    private static string? Compare(byte* p, RnHull? h)
    {
        if (p == null || h == null)
            return p == null && h == null ? null : $"valve {(p == null ? "null" : "a hull")}, ours {(h == null ? "null" : "a hull")}";
        int n = *(int*)(p + 0x70);
        var v = *(Vector3**)(p + 0x78);
        if (n != h.VertexPositions.Length)
            return $"{n} vertices, ours {h.VertexPositions.Length}";
        for (var i = 0; i < n; i++)
            if (Bits(v[i].X) != Bits(h.VertexPositions[i].X) || Bits(v[i].Y) != Bits(h.VertexPositions[i].Y) || Bits(v[i].Z) != Bits(h.VertexPositions[i].Z))
                return $"vertex {i}: {v[i]:R}, ours {h.VertexPositions[i]:R}";
        int np = *(int*)(p + 0x88);
        var planes = *(Vector4**)(p + 0x90);
        if (np != h.Planes.Length)
            return $"{np} planes, ours {h.Planes.Length}";
        for (var i = 0; i < np; i++)
            if (Bits(planes[i].X) != Bits(h.Planes[i].Normal.X) || Bits(planes[i].Y) != Bits(h.Planes[i].Normal.Y)
                || Bits(planes[i].Z) != Bits(h.Planes[i].Normal.Z) || Bits(planes[i].W) != Bits(h.Planes[i].Offset))
                return $"plane {i}";
        int ne = *(int*)(p + 0xc8);
        var edges = *(uint**)(p + 0xd0);
        if (ne != h.Edges.Length)
            return $"{ne} edges, ours {h.Edges.Length}";
        for (var i = 0; i < ne; i++)
        {
            var e = h.Edges[i];
            if (edges[i] != (uint)(e.Next | e.Twin << 8 | e.Origin << 16 | e.Face << 24))
                return $"edge {i}";
        }
        if (*(uint*)(p + 0xa0) != h.Flags)
            return $"flags {*(uint*)(p + 0xa0):x}, ours {h.Flags:x}";
        if (*(Vector3*)(p + 0x14) != h.BoundsMin || *(Vector3*)(p + 0x20) != h.BoundsMax)
            return "bounds";
        return null;
    }
}
