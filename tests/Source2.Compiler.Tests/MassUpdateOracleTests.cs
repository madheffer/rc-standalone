using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The mass update's pieces (<see cref="RnMassUpdate"/>) against vphysics2's
/// own functions on random input: the capsule and hull mass properties, the
/// point-mass shift and the inverse inertia, bit for bit.
/// </summary>
public sealed unsafe class MassUpdateOracleTests
{
    private const int Cases = 20000;

    [Fact]
    public void CapsuleMassMatchesValve()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var valve = (delegate* unmanaged<float*, float*, float*, float*>)Vphysics2Oracle.At(module, 0x180291d80);
        var random = new Random(7);
        var outBuf = (float*)NativeMemory.AlignedAlloc(0x40, 16);
        var capsule = (float*)NativeMemory.AlignedAlloc(0x20, 16);
        var material = (float*)NativeMemory.AlignedAlloc(0x20, 16);
        try
        {
            for (var n = 0; n < Cases; n++)
            {
                var a = Point(random);
                // Some capsules along an axis, some degenerate, most anywhere.
                var b = (n % 7) switch
                {
                    0 => a,
                    1 => a + new Vector3(0, 0, -(float)random.NextDouble() * 10),
                    2 => a + new Vector3(0, 0, (float)random.NextDouble() * 10),
                    _ => Point(random),
                };
                var r = (float)random.NextDouble() * 8 + 0.01f;
                var m = new ContactSolver.Material
                {
                    Density = n % 11 == 0 ? 0f : (float)random.NextDouble() * 0.05f,
                    Thickness = n % 2 == 0 ? 0f : (n % 3 == 0 ? -1f : (float)random.NextDouble()),
                };
                capsule[0] = a.X; capsule[1] = a.Y; capsule[2] = a.Z;
                capsule[3] = b.X; capsule[4] = b.Y; capsule[5] = b.Z;
                capsule[6] = r;
                *(ContactSolver.Material*)material = m;
                new Span<float>(outBuf, 16).Clear();
                valve(outBuf, capsule, material);
                var ours = RnMassUpdate.CapsuleProperties((a, b, r), m);
                AssertSame(outBuf, ours, $"case {n}: {a} {b} {r} density {m.Density} thickness {m.Thickness}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(outBuf);
            NativeMemory.AlignedFree(capsule);
            NativeMemory.AlignedFree(material);
        }
    }

    [Fact]
    public void HullMassMatchesValve()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var valve = (delegate* unmanaged<float*, byte*, float, float*, float*>)Vphysics2Oracle.At(module, 0x180292350);
        var random = new Random(11);
        var outBuf = (float*)NativeMemory.AlignedAlloc(0x40, 16);
        var hullBuf = (byte*)NativeMemory.AlignedAlloc(0x100, 16);
        var material = (float*)NativeMemory.AlignedAlloc(0x20, 16);
        try
        {
            for (var n = 0; n < Cases; n++)
            {
                var hull = new RnHull { Volume = (float)random.NextDouble() * 5000, SurfaceArea = (float)random.NextDouble() * 3000 };
                for (var i = 0; i < 12; i++)
                    hull.MassProperties[i] = (float)(random.NextDouble() * 200 - 100);
                new Span<byte>(hullBuf, 0x100).Clear();
                for (var i = 0; i < 12; i++)
                    *(float*)(hullBuf + 0x38 + 4 * i) = hull.MassProperties[i];
                *(float*)(hullBuf + 0x68) = hull.Volume;
                *(float*)(hullBuf + 0x6c) = hull.SurfaceArea;
                var m = new ContactSolver.Material
                {
                    Density = n % 13 == 0 ? 0f : (float)random.NextDouble() * 0.05f,
                    Thickness = n % 2 == 0 ? 0f : (float)random.NextDouble(),
                };
                *(ContactSolver.Material*)material = m;
                var s = n % 3 == 0 ? 1f : (float)random.NextDouble() * 3 + 0.1f;
                new Span<float>(outBuf, 16).Clear();
                valve(outBuf, hullBuf, s, material);
                AssertSame(outBuf, RnMassUpdate.Hull(hull, s, m), $"case {n}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(outBuf);
            NativeMemory.AlignedFree(hullBuf);
            NativeMemory.AlignedFree(material);
        }
    }

    [Fact]
    public void InverseInertiaMatchesValve()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var valve = (delegate* unmanaged<byte*, float*, byte, uint>)Vphysics2Oracle.At(module, 0x1801bb2b0);
        var random = new Random(13);
        var body = (byte*)NativeMemory.AlignedAlloc(0x280, 16);
        var inertia = (float*)NativeMemory.AlignedAlloc(0x40, 16);
        try
        {
            for (var n = 0; n < Cases; n++)
            {
                new Span<byte>(body, 0x280).Clear();
                var state = new RnBodyState { InertiaDivisor = (float)random.NextDouble() * 1000 + 0.01f };
                var m = new Mat3();
                for (var i = 0; i < 9; i++)
                    m[i] = n % 17 == 0 ? 0f : (float)(random.NextDouble() * 2e6 - 1e6) * (n % 5 == 0 ? 1e8f : 1f);
                for (var i = 0; i < 9; i++)
                    inertia[i] = m[i];
                *(RnBodyState*)body = state;
                valve(body, inertia, 0);
                RnMassUpdate.SetInertia(ref state, m);
                var theirs = ((RnBodyState*)body)->LocalInvInertia;
                for (var i = 0; i < 9; i++)
                    Assert.True(BitConverter.SingleToInt32Bits(theirs[i]) == BitConverter.SingleToInt32Bits(state.LocalInvInertia[i]),
                                $"case {n} element {i}: valve {theirs[i]:R} ours {state.LocalInvInertia[i]:R}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(body);
            NativeMemory.AlignedFree(inertia);
        }
    }

    private static Vector3 Point(Random r)
        => new((float)(r.NextDouble() * 200 - 100), (float)(r.NextDouble() * 200 - 100), (float)(r.NextDouble() * 200 - 100));

    private static void AssertSame(float* valve, RnMassUpdate.Properties ours, string what)
    {
        float[] mine = [ours.Inertia.M0, ours.Inertia.M1, ours.Inertia.M2, ours.Inertia.M3, ours.Inertia.M4, ours.Inertia.M5,
                        ours.Inertia.M6, ours.Inertia.M7, ours.Inertia.M8, ours.Center.X, ours.Center.Y, ours.Center.Z, ours.Mass];
        for (var i = 0; i < 13; i++)
            Assert.True(BitConverter.SingleToInt32Bits(valve[i]) == BitConverter.SingleToInt32Bits(mine[i]),
                        $"{what}: float {i} valve {valve[i]:R} ours {mine[i]:R}");
    }
}
