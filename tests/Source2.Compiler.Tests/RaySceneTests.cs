using System.Numerics;
using Source2.Compiler.Lighting.Bake;
using Xunit;

namespace Source2.Compiler.Tests;

public class RaySceneTests
{
    // One triangle in z = 0, counter-clockwise seen from +z: its face normal cross(v1 - v0, v2 - v0) is +z.
    private static RayScene OneTriangle(byte mask = 0xff) => new(
        [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
        [new RayScene.Triangle(0, 0)], [new RayScene.Instance(mask, true)]);

    [Fact]
    public void FrontFacingIsCounterClockwiseFromTheOrigin()
    {
        var scene = OneTriangle();
        var fromFront = scene.Trace(new Vector3(0.25f, 0.25f, 5), -Vector3.UnitZ, 0, 100, 0, 0xff);
        var fromBack = scene.Trace(new Vector3(0.25f, 0.25f, -5), Vector3.UnitZ, 0, 100, 0, 0xff);
        Assert.True(fromFront.Found && fromFront.FrontFacing);
        Assert.True(fromBack.Found && !fromBack.FrontFacing);
        Assert.Equal(5f, fromFront.T, 5);
        Assert.Equal(0.25f, fromFront.U, 5);
        Assert.Equal(0.25f, fromFront.V, 5);
        Assert.False(scene.Trace(new Vector3(0.25f, 0.25f, 5), -Vector3.UnitZ, 0, 100, RayScene.CullFrontFacing, 0xff).Found);
        Assert.False(scene.Trace(new Vector3(0.25f, 0.25f, -5), Vector3.UnitZ, 0, 100, RayScene.CullBackFacing, 0xff).Found);
        Assert.False(OneTriangle(1).Trace(new Vector3(0.25f, 0.25f, 5), -Vector3.UnitZ, 0, 100, 0, 2).Found);
        Assert.False(scene.Trace(new Vector3(0.25f, 0.25f, 5), -Vector3.UnitZ, 0, 4.9f, 0, 0xff).Found);
    }

    [Fact]
    public void ClosestHitMatchesBruteForce()
    {
        var rng = new Random(7);
        var verts = new List<Vector3>();
        var tris = new List<RayScene.Triangle>();
        for (var i = 0; i < 3000; i++)
        {
            var c = new Vector3(rng.NextSingle() * 100, rng.NextSingle() * 100, rng.NextSingle() * 100);
            for (var k = 0; k < 3; k++)
                verts.Add(c + new Vector3(rng.NextSingle() * 6 - 3, rng.NextSingle() * 6 - 3, rng.NextSingle() * 6 - 3));
            tris.Add(new RayScene.Triangle(0, i));
        }
        var scene = new RayScene([.. verts], [.. tris], [new RayScene.Instance(0xff, true)]);
        var brute = new RayScene([.. verts], [.. tris], [new RayScene.Instance(0xff, true)]);
        for (var r = 0; r < 2000; r++)
        {
            var o = new Vector3(rng.NextSingle() * 100, rng.NextSingle() * 100, rng.NextSingle() * 100);
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
            var hit = scene.Trace(o, d, 0, 1000, 0, 0xff);
            // Brute force over every triangle with one-triangle scenes.
            var best = 1000f;
            for (var i = 0; i < tris.Count; i++)
            {
                var one = new RayScene([verts[i * 3], verts[i * 3 + 1], verts[i * 3 + 2]], [new RayScene.Triangle(0, 0)], [new RayScene.Instance(0xff, true)]);
                var h = one.Trace(o, d, 0, best, 0, 0xff);
                if (h.Found)
                    best = h.T;
            }
            Assert.Equal(best < 1000f, hit.Found);
            if (hit.Found)
                Assert.Equal(best, hit.T, 4);
            if (r > 200)
                break;
        }
        _ = brute;
    }
}

public class HaltonSamplesTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>PBRT's construction: the first dimension, scaled by 32, gives back the texel's x for every frame.</summary>
    [Fact]
    public void FirstDimensionIsTheTexel()
    {
        for (var frame = 0u; frame < 3; frame++)
            for (var y = 0u; y < 32; y++)
                for (var x = 0u; x < 32; x++)
                {
                    var i = Source2.Compiler.Lighting.Bake.HaltonSamples.Index(x, y, frame);
                    var v = Source2.Compiler.Lighting.Bake.HaltonSamples.Dimension([], 0, i);
                    Assert.Equal(x, (uint)(v * 32f));
                }
    }

    /// <summary>Exploration (<c>CS2 core pak</c>): textures/dev/scrambled_halton.vdata's keys.</summary>
    [Fact]
    public void DumpPermutations()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        var core = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(pak))!, "core", "pak01_dir.vpk");
        using var package = new ValvePak.Package();
        package.Read(core);
        var entry = package.FindEntry("textures/dev/scrambled_halton.vdata_c");
        Assert.NotNull(entry);
        package.ReadEntry(entry!, out var bytes);
        using var resource = new ValveResourceFormat.Resource();
        resource.Read(new MemoryStream(bytes));
        var data = (ValveResourceFormat.ResourceTypes.BinaryKV3)resource.DataBlock!;
        foreach (var c in data.Data.Root.Children)
            output.WriteLine($"{c.Key}: {c.Value.ValueType} {(c.Value.ValueType == ValveKeyValue.KVValueType.BinaryBlob ? c.Value.AsBlob().Length : 0)}");
    }
}

public class PackedFrameTests
{
    /// <summary>PackTangentFrame then either decode gives the frame back within the quantisation (10-bit normal, 11-bit angle).</summary>
    [Fact]
    public void RoundTripsWithinTheQuantisation()
    {
        var random = new Random(3);
        for (var i = 0; i < 2000; i++)
        {
            var n = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var any = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var t = Vector3.Normalize(any - n * Vector3.Dot(any, n));
            var sign = i % 2 == 0 ? 1f : -1f;
            var frame = PackedFrame.Pack(n, new Vector4(t, sign));
            foreach (var (dn, dt) in new[] { PackedFrame.Unpack(frame), PackedFrame.UnpackGpu(frame) })
            {
                Assert.True(Vector3.Dot(dn, n) > 0.9999f, $"normal {n} -> {dn}");
                Assert.True(Vector3.Dot(new Vector3(dt.X, dt.Y, dt.Z), t) > 0.998f, $"tangent {t} -> {dt}");
                Assert.Equal(sign, dt.W);
            }
        }
    }
}
