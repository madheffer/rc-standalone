using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// vphysics2's box casts against what tools/re/capture_navcast.py recorded during Valve's nav compile
/// (<c>NAVCAST_DIR</c>, default %TEMP%/navgen_capture/s2c_prefabprobe3_in): every shape cast navsystem's
/// box casts made, with Valve's inputs, replayed through <see cref="BoxCast"/>.
/// </summary>
public sealed class NavCastTests(ITestOutputHelper output)
{
    private static string Dir() => Environment.GetEnvironmentVariable("NAVCAST_DIR")
                                   ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "s2c_prefabprobe3_in");

    internal sealed record CapturedMesh(RnMesh Mesh, Vector3 Scale, byte ShapeFlag, int Wings);

    internal sealed record CapturedHull(Vector3 Reference, Vector3[] Points, float Scale, byte[] Header);

    private static Vector3 V3(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
                                                           BinaryPrimitives.ReadSingleLittleEndian(b[8..]));

    /// <summary>The shapes navcast_shapedata.bin dumped, by pointer.</summary>
    internal static (Dictionary<ulong, CapturedMesh> Meshes, Dictionary<ulong, CapturedHull> Hulls) Shapes(string dir, string file = "navcast_shapedata.bin")
    {
        var b = File.ReadAllBytes(Path.Combine(dir, file));
        var meshes = new Dictionary<ulong, CapturedMesh>();
        var hulls = new Dictionary<ulong, CapturedHull>();
        var at = 0;
        while (at + 4 <= b.Length)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
            var r = b.AsSpan(at + 4, len);
            at += 4 + len;
            var ptr = BinaryPrimitives.ReadUInt64LittleEndian(r);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
            var p = r[12..];
            if (type == 3)
            {
                var scale = V3(p);
                var flag = p[12];
                var h = p.Slice(13, 0xc0);
                int nn = BinaryPrimitives.ReadInt32LittleEndian(h[0x18..]), nv = BinaryPrimitives.ReadInt32LittleEndian(h[0x30..]),
                    nt = BinaryPrimitives.ReadInt32LittleEndian(h[0x48..]), wings = BinaryPrimitives.ReadInt32LittleEndian(h[0x60..]);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(h[0xb4..]);
                var q = p[(13 + 0xc0)..];
                var nodes = new RnMesh.Node[nn];
                for (var i = 0; i < nn; i++)
                {
                    var n = q.Slice(i * 0x20, 0x20);
                    nodes[i] = new RnMesh.Node(V3(n), BinaryPrimitives.ReadUInt32LittleEndian(n[12..]), V3(n[16..]), BinaryPrimitives.ReadUInt32LittleEndian(n[28..]));
                }
                q = q[(nn * 0x20)..];
                var verts = new Vector3[nv];
                for (var i = 0; i < nv; i++)
                    verts[i] = V3(q[(i * 12)..]);
                q = q[(nv * 12)..];
                var tris = new (int, int, int)[nt];
                for (var i = 0; i < nt; i++)
                    tris[i] = (BinaryPrimitives.ReadInt32LittleEndian(q[(i * 12)..]), BinaryPrimitives.ReadInt32LittleEndian(q[(i * 12 + 4)..]),
                               BinaryPrimitives.ReadInt32LittleEndian(q[(i * 12 + 8)..]));
                var mesh = new RnMesh { Min = V3(h), Max = V3(h[12..]), Nodes = nodes, Vertices = verts, Triangles = tris, Flags = flags };
                meshes[ptr] = new CapturedMesh(mesh, scale, flag, wings);
            }
            else if (type == 2)
            {
                var scale = BinaryPrimitives.ReadSingleLittleEndian(p);
                var head = p.Slice(4, 0x80).ToArray();
                var n = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x70));
                var pts = new Vector3[n];
                for (var i = 0; i < n; i++)
                    pts[i] = V3(p[(4 + 0x80 + i * 12)..]);
                hulls[ptr] = new CapturedHull(V3(head), pts, scale, head);
            }
        }
        return (meshes, hulls);
    }

    /// <summary>One shape cast record (0x80 bytes).</summary>
    internal readonly record struct ShapeCast(uint Type, ulong Shape, float[] Transform, Vector3 Centre, Vector3 Delta, Vector3 Half,
                                              float MaxFraction, bool Returned, float Fraction, uint World);

    internal static IEnumerable<ShapeCast> ShapeCasts(string dir, int limit = int.MaxValue)
    {
        using var f = File.OpenRead(Path.Combine(dir, "navcast_shapes.bin"));
        var buf = new byte[0x80];
        for (var i = 0; i < limit && f.Read(buf, 0, 0x80) == 0x80; i++)
        {
            var t = new float[12];
            for (var k = 0; k < 12; k++)
                t[k] = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(0x10 + k * 4));
            yield return new ShapeCast(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)), BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8)), t,
                V3(buf.AsSpan(0x40)), V3(buf.AsSpan(0x4c)), V3(buf.AsSpan(0x58)), BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(0x64)),
                buf[0x68] != 0, BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(0x6c)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x70)));
        }
    }

    // 1802421c0: the unscaled path needs no shape flag and an identity diagonal with no translation.
    private static bool Unscaled(CapturedMesh m, float[] t)
        => m.ShapeFlag == 0 && t[0] == 1f && t[4] == 1f && t[8] == 1f && t[9] == 0f && t[10] == 0f && t[11] == 0f;

    [Fact]
    public void MeshBoxCastsMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navcast_shapedata.bin")))
            return;
        var (meshes, _) = Shapes(dir);
        int total = 0, same = 0, missing = 0, shown = 0, closedSkipped = 0;
        var limit = int.TryParse(Environment.GetEnvironmentVariable("NAVCAST_LIMIT"), out var l) ? l : 200000;
        foreach (var c in ShapeCasts(dir, limit))
        {
            if (c.Type != 3)
                continue;
            if (!meshes.TryGetValue(c.Shape, out var m))
            {
                missing++;
                continue;
            }
            // Triangles with three wings take 180243770, which differs from 180244380 only in the
            // normal it writes (the block at 180243f19 .. 180244367); the fraction is the same.
            var closed = (m.Mesh.Flags & 1) != 0;
            var hit = Unscaled(m, c.Transform)
                ? BoxCast.MeshUnscaled(m.Mesh, c.Centre, c.Delta, c.Half, c.MaxFraction)
                : BoxCast.MeshTransformed(m.Mesh, m.Scale, c.Transform, c.Centre, c.Delta, c.Half, c.MaxFraction);
            if (closed)
                closedSkipped++;
            total++;
            var ours = hit.Found ? hit.Fraction : 1f;
            if (hit.Found == c.Returned && BitConverter.SingleToInt32Bits(ours) == BitConverter.SingleToInt32Bits(c.Fraction))
                same++;
            else if (shown++ < 10)
                output.WriteLine($"cast {total}: ours {hit.Found} {ours:R}, Valve {c.Returned} {c.Fraction:R}, max {c.MaxFraction:R}");
        }
        output.WriteLine($"{same} of {total} mesh casts Valve's ({missing} on shapes not dumped, {closedSkipped} on closed meshes)");
        Assert.Equal(total, same);
    }

    [Fact]
    public void HullBoxCastsMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navcast_shapedata.bin")))
            return;
        var (_, hulls) = Shapes(dir);
        int total = 0, same = 0, missing = 0, shown = 0, hits = 0;
        foreach (var c in ShapeCasts(dir))
        {
            if (c.Type != 2)
                continue;
            if (!hulls.TryGetValue(c.Shape, out var h))
            {
                missing++;
                continue;
            }
            total++;
            var hit = HullCast.Cast(h.Reference, h.Points, h.Scale, c.Transform, c.Centre, c.Delta, c.Half);
            if (c.Returned)
                hits++;
            if (hit.Found == c.Returned && (!hit.Found || BitConverter.SingleToInt32Bits(hit.Fraction) == BitConverter.SingleToInt32Bits(c.Fraction)))
                same++;
            else if (shown++ < 15)
                output.WriteLine($"cast {total}: ours {hit.Found} {hit.Fraction:R}{(hit.StartSolid ? " solid" : "")}, Valve {c.Returned} {c.Fraction:R}; " +
                                 $"start {c.Centre} delta {c.Delta} half {c.Half}");
        }
        output.WriteLine($"{same} of {total} hull casts Valve's ({hits} hits; {missing} on shapes not dumped)");
        Assert.Equal(total, same);
    }
}
